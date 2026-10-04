// odx <command> [args]: the capture-cs command-line tools in one executable.
//   input      keyboard/mouse with Raw Input (Windows build only)
//   record     game window or monitor with Windows.Graphics.Capture + ffmpeg (Windows build only)
//   build      capture folder -> spec v2 session (frames.csv + session.json)
//   validate   spec v2 §1.5 vendor checks
// The net8.0 build (Linux servers) has build and validate only.
using System.Text;
using Odx.Cli;

Console.OutputEncoding = new UTF8Encoding(false);
string command = args.Length > 0 ? args[0] : "";
string[] rest = args.Length > 0 ? args[1..] : [];
return command switch
{
#if WINDOWS
    "input" => Odx.Input.InputCommand.Run(rest),
    "record" => Odx.Recorder.RecordCommand.Run(rest),
#endif
    "build" => BuildCommand.Run(rest),
    "validate" => ValidateCommand.Run(rest),
    _ => Usage(),
};

static int Usage()
{
#if WINDOWS
    Console.Error.WriteLine("usage: odx (input | record | build | validate) [args]");
#else
    Console.Error.WriteLine("usage: odx (build | validate) [args]");
#endif
    return 2;
}
