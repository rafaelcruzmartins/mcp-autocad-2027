// UseWPF removes System.IO from implicit usings (clash with System.Windows.Shapes.Path).
global using System.IO;
global using Path = System.IO.Path;
