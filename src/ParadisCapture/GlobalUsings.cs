// The WPF/WinForms SDK deliberately leaves System.IO out of its implicit usings (System.IO.Path
// would clash with System.Windows.Shapes.Path in XAML code-behind). None of this project's C# files
// use the WPF shapes namespace, so importing System.IO globally is unambiguous here.
global using System.IO;
