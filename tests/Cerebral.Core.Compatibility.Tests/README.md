# Core compatibility checks

This isolated console test checks the installed 0.2.22 constructor and deconstruction
signatures and JSON deserialization of the extended schemas. It neither starts
Cerebral nor reads settings or databases.

The default project reference permits ordinary source regression checks. For an
actual backward binary-call check, compile against an exact retained older
Cerebral.Core.dll using `-p:CoreReferencePath=<absolute old reference path>`.
The reference has `Private=false`: the test executable accepts the exact candidate
Core DLL as its only runtime argument and loads it before binding its Core-typed
method. Run the same compiled test executable against the candidate without
recompiling between reference generations.

Build and run with the existing local .NET SDK and package sources. For example:

```powershell
dotnet build tests/Cerebral.Core.Compatibility.Tests/Cerebral.Core.Compatibility.Tests.csproj -c Release -p:CoreReferencePath="C:/path/to/retained/Cerebral.Core.dll" --artifacts-path build/CoreCompatibilityTests
dotnet build src/Cerebral.Core/Cerebral.Core.csproj -c Release --artifacts-path build/CoreCompatibilityCandidate
dotnet build/CoreCompatibilityTests/bin/Cerebral.Core.Compatibility.Tests/release/Cerebral.Core.Compatibility.Tests.dll "C:/absolute/candidate/Cerebral.Core.dll"
```

Explicit reflection assertions also retain the old CLR signatures when the test is
compiled against current source. Passing checks are component ABI/JSON evidence;
they do not prove installed plugin initialization, workflow recovery, or deployment.
