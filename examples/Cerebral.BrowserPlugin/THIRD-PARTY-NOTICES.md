# Browser package dependencies and provenance

Microsoft.Playwright 1.63.0 (.NET API) declares MIT in its exact NuGet package metadata and identifies Microsoft Corporation as copyright owner:
https://github.com/microsoft/playwright-dotnet

The included playwright-core 1.63.0 JavaScript driver is a separate Apache-2.0 component. Its actual LICENSE, NOTICE, ThirdPartyNotices.txt and bundled library license files remain beneath .playwright/package. The Node runtime license and its third-party notices remain in .playwright/node/LICENSE.

Microsoft.Bcl.AsyncInterfaces 6.0.0 declares MIT. Its exact cached license and third-party notices are included in Licensing.Microsoft.Bcl.AsyncInterfaces-6.0.0.LICENSE.md and Licensing.Microsoft.Bcl.AsyncInterfaces-6.0.0.NOTICES.md.

Licensing.Microsoft.Playwright-1.63.0.NUSPEC.md retains the publisher's exact NuGet metadata, including its MIT declaration, copyright and repository commit, with README.md provenance in Licensing.Microsoft.Playwright-1.63.0.README.md and attributed terms in Licensing.Microsoft.Playwright-1.63.0.MIT-TERMS.md. The declaration is not inferred from the JavaScript driver's different license.

The NuGet package includes its driver runtime and additional third-party notices. Preserve the complete package output, including its hidden `.playwright` driver directory and all upstream license/notice files. Do not select only the DLL.

Browser Use 0.1.40, Copyright (c) 2024 Gregor Zunic, MIT, was reviewed as an architectural reference. No Browser Use source or Python agent implementation is included in this plugin. Its license does not license Chrome, Edge, or unrelated browser binaries.

No browser binary ships in this source package. A future Chromium resource must carry its exact version, architecture, upstream download URL, Chromium license, complete third-party notices and any distribution-specific terms. Chromium source licensing is at https://chromium.googlesource.com/chromium/src/+/main/LICENSE . That source license alone does not prove the terms of a particular downloadable binary. Microsoft.Playwright 1.63.0's default browser descriptor labels its Chromium-family downloads “Chrome for Testing” and “Chrome Headless Shell”; this package does not download or redistribute them.
