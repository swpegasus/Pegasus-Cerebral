PEGASUS CEREBRAL BRANDING RESOURCES

These files retain the blue-white wing/network identity. Runtime surfaces use named derivatives and do not assume an absolute installation path.

cerebral-art-source.png        Existing generated poster master, retained unchanged.
cerebral-appmark-master.png    Generated square appmark derived from that master: an unobstructed wing and neural network on a clean navy-blue tile.
cerebral-splash-sidebar.png    Existing desktop loading-splash artwork, retained unchanged.
cerebral-installer-sidebar.bmp Existing NSIS welcome/finish artwork, retained unchanged.
cerebral-watermark.png         Existing neutral transparent alpha mask, retained unchanged.
cerebral-*.ico                 Multi-resolution Windows icons using the shared unobstructed appmark.
icons/                         Proportional PNG size ladders from 16 through 1024 pixels.
brand.ini                      Packaging resource map. Paths are relative to this folder; the desktop does not read this map at runtime.

All three stable role filenames use the same Pegasus Cerebral appmark. Application display names identify the role. There are no generic status, sliders, share or processor badges covering the brand artwork. The appmark contains no text.

The square appmark was created with the built-in image generation editing tool from the existing repository artwork. PNG size ladders and ICO frames use proportional resizing and encoding only. No recoloring, clipping, badge painting, or alpha editing was applied during packaging. The original artwork, splash, installer bitmap and watermark are byte-preserved.

The desktop embeds artwork as WPF resources. Its window and header icon use assets/branding/cerebral-*.ico. The watermark supplies alpha only; the active theme supplies the tint at the point of use. Portable color presets imported through Pegasus Appearance remain independent of these artwork files. Importing a preset does not replace host artwork; an artwork change requires rebuilding the host.

scripts/build-branding-assets.py validates and consumes cerebral-appmark-master.png, then proportionally resizes and encodes the listed PNG/ICO sizes and existing mirror locations. It does not regenerate or modify the authored poster, splash, installer bitmap, neutral watermark or metadata. The portable color-theme workflow remains the Pegasus Appearance plugin.

Provenance: the existing master is repository product artwork. This derivative adds no third-party icon set, font artwork, or production dependency. No new license grant is asserted for the original artwork.
