using System.Runtime.CompilerServices;

// The stackalloc'd UTF-8 and batch buffers are always written before they are read.
[module: SkipLocalsInit]
