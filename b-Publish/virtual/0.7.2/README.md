# AppShell 0.7.2 virtual publish

This directory validates the historical packages, consumer-document generation, manifest, and SHA-256 release chain. It is not the formal feed and declares compatibilityValidated=false. The z-Package-AppShell root remains the formal entry point.

- feed/: four 0.7.2 packages copied from the immutable b-Publish archive after identity validation.
- docs/: generated from the current consumer-contract sources with a virtual-publish warning.
- AppShell.reuse.md: generated from the release template with a virtual-publish warning.
- manifest.json and SHA256SUMS: the virtual release manifest and file checksums.