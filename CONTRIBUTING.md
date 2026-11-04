# Contributing to SoftNodes

Thank you for wanting to help.

SoftNodes is **dual licensed**: AGPL-3.0 for everyone, and a commercial license that FactoryNodes offers to those who cannot use the AGPL.
To keep that possible, FactoryNodes needs the right to distribute every contribution under both licenses. So **before a pull request can be
merged, its author has to accept the [SoftNodes contributor license agreement (CLA)](CLA.md)**. You keep the copyright of your code; the CLA only
grants FactoryNodes the right to use it in SoftNodes and in its commercial editions.

To accept it, add this comment to your first pull request:

> I have read the SoftNodes CLA (version 1.0) and I agree to it, for myself or for the entity I represent: *<name, and entity if any>*.

For a large change, please open an issue first to tell us what you would like to do.

- Keep the existing layout: logic in the libraries (`src/libs`), the window only paints (`src/apps/SoftNodes.Open`).
- Every on-screen text goes through `L.T("…")` / `{l:T '…'}` and is translated into the five other languages.
- `dotnet test SoftNodes.sln` must pass.
