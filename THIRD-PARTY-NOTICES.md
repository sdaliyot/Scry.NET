# Third-party notices

Scry.NET is distributed under the MIT license (see [`LICENSE`](LICENSE)) and directly references
the packages below. All are published by Microsoft or the .NET Foundation and are themselves
MIT-licensed; the single license text that applies to every one of them is reproduced once at the
end of this file rather than five times over.

| Package | Version | Used by |
|---|---|---|
| [`Microsoft.CodeAnalysis.CSharp.Scripting`](https://github.com/dotnet/roslyn) | 4.14.0 | `Scry.Runtime` - the Roslyn scripting engine behind `evaluate`/`execute`/`wait`/`assert`. |
| [`System.Text.Json`](https://github.com/dotnet/runtime) | 9.0.11 | `Scry.Contracts`, `Scry.Wpf`, `Scry.WinForms` - the wire protocol's JSON serialization on .NET Framework. |
| [`Microsoft.Bcl.AsyncInterfaces`](https://github.com/dotnet/runtime) | 9.0.11 | Every project targeting .NET Framework - backfills `IAsyncEnumerable`/`IAsyncDisposable` support. |
| [`System.Threading.Tasks.Extensions`](https://github.com/dotnet/runtime) | 4.5.4 | Every project targeting .NET Framework - backfills `ValueTask`/`ValueTask<T>`. |
| [`Microsoft.NETFramework.ReferenceAssemblies.net462`](https://github.com/dotnet/sdk) | 1.0.3 | Every net462 build - makes SDK-style net462 builds reproducible without a machine-installed targeting pack; contributes no runtime code. |

**What this list does not attempt to enumerate.** `Microsoft.CodeAnalysis.CSharp.Scripting` pulls
in further Microsoft/.NET Foundation packages of its own (the rest of the Roslyn compiler platform,
plus small BCL polyfills such as `System.Collections.Immutable` and `System.Reflection.Metadata`).
Every one of them is MIT-licensed under the same `dotnet/roslyn` or `dotnet/runtime` projects as the
packages above, so the license terms are identical - but the exact transitive set shifts with every
Roslyn version bump, and pinning an exhaustive list here would go stale faster than anyone would
notice. Run `dotnet list package --include-transitive` against `src/Scry.Runtime` for the current,
exact set at any given commit.

Versions above track `Directory.Build.props` at the time of writing; that file is authoritative if
the two ever disagree.

---

## MIT License

The license below applies to every package listed above.

```
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

Copyright (c) .NET Foundation and Contributors.
