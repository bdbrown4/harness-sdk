#if NET8_0_OR_GREATER
// A precompiled netstandard2.0 extension references each assembly's compatibility
// marker in init setter signatures. Forward it to the modern runtime marker.
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(System.Runtime.CompilerServices.IsExternalInit))]
#endif
