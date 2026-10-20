# Third-party notices

## DirectN

This project uses **DirectNCore** by Simon Mourier for Media Foundation, COM and Windows interop.

- Project: https://github.com/smourier/DirectN
- License: MIT
- Copyright © Simon Mourier

The NuGet package `DirectNCore` 1.17.3 is consumed as a normal package reference and is not modified by this repository.

## VCamNetSample / VCamSample

The structure of the Media Foundation virtual camera source (IMFActivate / IMFMediaSourceEx / IMFMediaStream2 and the virtual camera registration flow) was informed by Simon Mourier's Windows 11 virtual camera samples:

- https://github.com/smourier/VCamNetSample
- https://github.com/smourier/VCamSample
- License: MIT

The frame transport in this project (shared-memory seqlock between the WPF process and the Frame Server media source) is original to this repository.

## Microsoft documentation

- `MFCreateVirtualCamera`: https://learn.microsoft.com/windows/win32/api/mfvirtualcamera/nf-mfvirtualcamera-mfcreatevirtualcamera
- `IMFVirtualCamera`: https://learn.microsoft.com/windows/win32/api/mfvirtualcamera/nn-mfvirtualcamera-imfvirtualcamera
