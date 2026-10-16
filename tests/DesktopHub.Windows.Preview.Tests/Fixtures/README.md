# Preview fixtures

The five WebP files are synthetic test images generated with the official
libwebp 1.6.0 Windows x64 tools. They are committed so the default build and
preview tests do not require an untracked encoder installation or network access.
No third-party image content is included.

To regenerate, create 40 x 20 PNG images using the harness's `WriteImage` helper:
`blue.png` uses opaque blue (A=255, R=0, G=0, B=255), and `alpha.png` uses
half-transparent red (A=128, R=255, G=0, B=0). Then run:

```text
cwebp -lossless blue.png -o lossless.webp
cwebp -q 95 blue.png -o lossy.webp
cwebp -lossless alpha.png -o alpha.webp
webpmux -frame alpha.webp +100+10+4+0+b -frame lossless.webp +100+0+0+0+b -bgcolor 255,0,255,0 -o blend.webp
webpmux -frame alpha.webp +100+10+4+0-b -frame lossless.webp +100+0+0+0+b -bgcolor 255,0,255,0 -o replace.webp
```

The harness checks decoded dimensions, blue pixels, alpha, animation canvas,
green background, and first-frame blend/replace behavior through the product
decoder. The encoders are only needed when intentionally regenerating fixtures.
