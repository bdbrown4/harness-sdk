# Python presentation Unicode data

Python result presentation uses the retained CPython 3.10.2 / Unicode 13.0.0
reference environment. It does not depend on the host .NET Unicode database or
claim to match the Unicode updates in every subsequent Python minor version.

`PythonUnicode.cs` contains 679 inclusive intervals of code points for which
`str.isprintable()` is false. The private reference generator compared every
one of the 1,114,112 code points against both `str.isprintable()` and the Unicode
general-category rule, then independently checked interval membership for every
code point. No CPython implementation source was copied.

Reference data SHA256:
`911D25BA1BEF46F9ECFD8C6D1559DF07FA587097469869DF9F22F8534418BC4F`.

The derived Unicode data is accompanied by the official Unicode License V3,
retrieved from <https://www.unicode.org/license.txt>. This notice and the full
license are included in the Strands.Agents NuGet package.
