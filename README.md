<div align="center">

<img src="ScreenBridge.ico" width="72" alt="ScreenBridge icon">

# ScreenBridge

**Shows my Windows PC's screen on my Mac and the other way around. You can control both computers, send files, and the connection is encrypted.**

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![Encryption](https://img.shields.io/badge/connection-TLS%201.3-6E40C9)
![Delay](https://img.shields.io/badge/delay-about%2063%20ms-2EA44F)

</div>

## What is this?

After I got my Mac's screen onto my PC with [AirPlay Receiver](https://github.com/businessbaum/AirPlayReceiver), I wanted the opposite direction too. AirPlay can't do that from Windows, because the sending side of AirPlay is protected. So I made my own protocol instead.

This repository contains the Windows side. It records the screen with the graphics card, compresses it to H.264 and sends it to an app on the Mac. The Mac app is not in this repository.

## How it works

- There is only one network port, and both computers have to prove who they are with a certificate.
- The first time you connect, both screens show the same six digits, like Bluetooth pairing. You check that they match.
- One connection is used for control messages and input, one for the video and one for each file you send.

## Making it fast

At the start the delay was about 103 ms. Now it is about 63 ms. Most of that came from leaving work out, not from making things faster:

- Only send the pixels the window really shows. This saved about 33 ms.
- Don't scale the picture if it already has the right size.
- Draw the mouse cursor on the local side instead of sending it through the video.

Some ideas that sounded good made it worse. Hardware decoding was slower here, and a "low latency" encoder setting more than doubled the delay.

## Things I learned

On one day I found seven bugs, and none of them were in the hard parts like encryption or video. Every single one was a piece of information that didn't arrive. For example, a status was only sent when it changed, so after a reconnect nobody knew it. Since then my rule is: test the simple things too, not just the parts that look hard.

## Run it

```powershell
dotnet run --project ScreenBridge.csproj
```

You need .NET 10, ffmpeg and an NVIDIA graphics card. The app runs in the tray and can start with Windows.

## Notes

The code comments and the protocol description PROTOKOLL.md are in German.

## License

MIT, see [LICENSE](LICENSE).
