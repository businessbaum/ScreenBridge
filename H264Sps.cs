namespace ScreenBridge;

/// <summary>
/// Liest die Bildmaße aus einer H.264-Sequenzkopfdatenmenge (SPS).
/// </summary>
/// <remarks>
/// <para>
/// <b>Warum nicht der Paketkopf.</b> Der Spiegelungsstrom nennt in seinem 128-Byte-Kopf
/// zwei Gleitkommazahlen, und die sahen lange wie die Bildgröße aus. Sie sind es nicht:
/// dort steht die Größe des <i>Bildschirms</i>, auf den der Mac zielt. Nachgemessen an
/// einer Sitzung stand dort 2560×1440, während der Strom tatsächlich <b>1920×1080</b>
/// enthielt — der Mac kodiert in 1080p und überlässt das Hochrechnen dem Empfänger.
/// Wir haben deshalb ein 1080p-Bild auf 1440p gerechnet und uns über die weiche
/// Darstellung gewundert.
/// </para>
/// <para>
/// Die echte Größe steht nur in der SPS. Sie zu lesen heißt, ein paar Felder mit
/// Exp-Golomb-Kodierung zu überspringen; abkürzen lässt sich das nicht, weil die Felder
/// unterschiedlich lang sind und die Maße am Ende stehen.
/// </para>
/// <para>
/// Geprüft gegen die Aufnahme einer echten Sitzung: dieselbe SPS ergibt hier 1920×1080
/// und bei <c>ffprobe</c> 1920×1080.
/// </para>
/// </remarks>
internal static class H264Sps
{
    /// <summary>
    /// Die Bildmaße aus einer SPS, einschließlich des NAL-Kopfbytes.
    /// </summary>
    public static bool TryGetSize(ReadOnlySpan<byte> nal, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (nal.Length < 5 || (nal[0] & 0x1F) != 7) return false;

        try
        {
            var bits = new BitReader(Unescape(nal[1..]));

            var profile = bits.Read(8);
            bits.Read(8);                 // Zwangsbedingungen und reservierte Bits
            bits.Read(8);                 // level_idc
            bits.ReadUnsigned();          // seq_parameter_set_id

            var chroma = 1u;
            if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                chroma = bits.ReadUnsigned();
                if (chroma == 3) bits.Read(1);   // separate_colour_plane_flag

                bits.ReadUnsigned();             // bit_depth_luma_minus8
                bits.ReadUnsigned();             // bit_depth_chroma_minus8
                bits.Read(1);                    // qpprime_y_zero_transform_bypass_flag

                if (bits.Read(1) == 1) SkipScalingMatrix(bits, chroma == 3 ? 12 : 8);
            }

            bits.ReadUnsigned();                 // log2_max_frame_num_minus4

            var pocType = bits.ReadUnsigned();
            if (pocType == 0)
            {
                bits.ReadUnsigned();             // log2_max_pic_order_cnt_lsb_minus4
            }
            else if (pocType == 1)
            {
                bits.Read(1);                    // delta_pic_order_always_zero_flag
                bits.ReadSigned();               // offset_for_non_ref_pic
                bits.ReadSigned();               // offset_for_top_to_bottom_field
                var cycle = bits.ReadUnsigned();
                for (var i = 0u; i < cycle; i++) bits.ReadSigned();
            }

            bits.ReadUnsigned();                 // max_num_ref_frames
            bits.Read(1);                        // gaps_in_frame_num_value_allowed_flag

            var widthInMacroblocks = bits.ReadUnsigned() + 1;
            var heightInUnits = bits.ReadUnsigned() + 1;
            var frameMbsOnly = bits.Read(1);

            if (frameMbsOnly == 0) bits.Read(1); // mb_adaptive_frame_field_flag
            bits.Read(1);                        // direct_8x8_inference_flag

            uint left = 0, right = 0, top = 0, bottom = 0;
            if (bits.Read(1) == 1)
            {
                left = bits.ReadUnsigned();
                right = bits.ReadUnsigned();
                top = bits.ReadUnsigned();
                bottom = bits.ReadUnsigned();
            }

            // Der Beschnitt zählt in Farbabtastpunkten, nicht in Bildpunkten.
            var unitX = chroma is 1 or 2 ? 2u : 1u;
            var unitY = (chroma == 1 ? 2u : 1u) * (2 - (uint)frameMbsOnly);

            width = (int)(widthInMacroblocks * 16 - (left + right) * unitX);
            height = (int)((2 - (uint)frameMbsOnly) * heightInUnits * 16 - (top + bottom) * unitY);

            // 8192 deckt jedes echte Display. Alles darüber ist ein Fehler — oder der
            // Versuch, den Empfänger einen Rahmen dieser Größe anlegen zu lassen.
            return width is > 0 and <= 8192 && height is > 0 and <= 8192;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Die Schutzbytes entfernen: eine <c>00 00 03</c>-Folge steht für <c>00 00</c>,
    /// damit im Rumpf kein Startcode entstehen kann.
    /// </summary>
    private static byte[] Unescape(ReadOnlySpan<byte> data)
    {
        var output = new byte[data.Length];
        var length = 0;
        var zeros = 0;

        foreach (var b in data)
        {
            if (zeros == 2 && b == 3) { zeros = 0; continue; }
            zeros = b == 0 ? zeros + 1 : 0;
            output[length++] = b;
        }

        return output[..length];
    }

    private static void SkipScalingMatrix(BitReader bits, int lists)
    {
        for (var i = 0; i < lists; i++)
        {
            if (bits.Read(1) == 0) continue;

            var last = 8;
            var next = 8;
            var size = i < 6 ? 16 : 64;

            for (var k = 0; k < size; k++)
            {
                if (next != 0) next = (last + (int)bits.ReadSigned() + 256) % 256;
                last = next == 0 ? last : next;
            }
        }
    }

    /// <summary>Bitweises Lesen, mit den beiden Exp-Golomb-Formen.</summary>
    private sealed class BitReader(byte[] data)
    {
        private int _position;

        public uint Read(int count)
        {
            uint value = 0;
            for (var i = 0; i < count; i++)
            {
                if (_position >= data.Length * 8) throw new InvalidOperationException("SPS zu kurz");
                value = (value << 1) | (uint)((data[_position >> 3] >> (7 - (_position & 7))) & 1);
                _position++;
            }

            return value;
        }

        public uint ReadUnsigned()
        {
            var zeros = 0;
            while (Read(1) == 0)
            {
                zeros++;
                if (zeros > 31) throw new InvalidOperationException("SPS unplausibel");
            }

            return (uint)((1 << zeros) - 1) + (zeros > 0 ? Read(zeros) : 0);
        }

        public int ReadSigned()
        {
            var k = ReadUnsigned();
            return (k & 1) == 1 ? (int)((k + 1) / 2) : -(int)(k / 2);
        }
    }
}
