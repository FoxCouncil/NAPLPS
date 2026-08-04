// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

// Blit an RGBA8888 framebuffer into a canvas. The byte array arrives from .NET as a
// Uint8Array; ImageData wants Uint8ClampedArray over the same buffer, no copy.
window.naplpsBlit = (canvas, width, height, rgba) => {
    if (canvas.width !== width) { canvas.width = width; }
    if (canvas.height !== height) { canvas.height = height; }

    const ctx = canvas.getContext('2d');
    ctx.putImageData(new ImageData(new Uint8ClampedArray(rgba.buffer, rgba.byteOffset, rgba.byteLength), width, height), 0, 0);
};
