using Fraimic.Api;
using Microsoft.Extensions.Logging;

// Fraimic image tool: convert an image to the 1600x1200 4-bit .bin format and optionally upload it.
//
// Usage:
//   FraimicBin <input-image> [-o output.bin] [--fit fill|fit|stretch] [--upload [host]]
//
// Examples:
//   FraimicBin photo.jpg                       -> photo.bin (full color, fill)
//   FraimicBin photo.jpg --upload              -> convert + send to fraimic.local
//   FraimicBin photo.jpg --upload 192.168.1.42 -> convert + send to that IP

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        Fraimic image tool

        Usage:
          FraimicBin <input-image> [-o output.bin] [--frame standard|large | --size WxH]
                                   [--fit fill|fit|stretch] [--upload [host]] [--log file]
          FraimicBin --test-pattern --frame large [--upload]   (orientation test card, no input)

        Options:
          -o <file>           Output .bin path (default: input name with .bin extension)
          --frame <model>     standard (1600x1200, default) | large (2560x1440)
          --size <WxH>        Explicit pixel size, overrides --frame (e.g. 1600x1200)
          --orientation <o>   landscape (default) | portrait | portrait-ccw | flip
                              (portrait = portrait-cw; if the test card is upside down, use portrait-ccw)
          --fit <mode>        fill (cover+crop, default) | fit (letterbox) | stretch
          --test-pattern      Generate an orientation test card for the frame (no input image)
          --upload [host]     Upload after converting (default host: fraimic.local)
          --log <file>        Log file path (default: logs/fraimic.log next to the app)
        """);
    return 0;
}

string? input = null;
string? output = null;
FrameSize size = FrameSize.StandardCanvas;
FrameOrientation orientation = FrameOrientation.Landscape;
FitMode fit = FitMode.Fill;
bool testPattern = false;
bool upload = false;
string host = "fraimic.local";
string logPath = Path.Combine(AppContext.BaseDirectory, "logs", "fraimic.log");

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-o" or "--output":
            output = RequireValue(args, ref i, "-o");
            break;
        case "--frame":
            string model = RequireValue(args, ref i, "--frame");
            size = model.ToLowerInvariant() switch
            {
                "standard" or "standardcanvas" => FrameSize.StandardCanvas,
                "large" or "largecanvas" => FrameSize.LargeCanvas,
                _ => throw new ArgumentException($"Unknown frame '{model}'. Use standard or large (or --size WxH)."),
            };
            break;
        case "--size":
            size = FrameSize.Parse(RequireValue(args, ref i, "--size"));
            break;
        case "--orientation" or "--orient":
            string o = RequireValue(args, ref i, "--orientation");
            orientation = o.ToLowerInvariant() switch
            {
                "landscape" or "land" => FrameOrientation.Landscape,
                "portrait" or "portrait-cw" or "portraitcw" or "cw" => FrameOrientation.PortraitClockwise,
                "portrait-ccw" or "portraitccw" or "ccw" => FrameOrientation.PortraitCounterClockwise,
                "flip" or "180" or "upside-down" => FrameOrientation.LandscapeUpsideDown,
                _ => throw new ArgumentException($"Unknown orientation '{o}'. Use landscape, portrait, portrait-ccw, or flip."),
            };
            break;
        case "--fit":
            string f = RequireValue(args, ref i, "--fit");
            fit = Enum.Parse<FitMode>(f, ignoreCase: true);
            break;
        case "--upload":
            upload = true;
            // Optional host follows, unless the next token is another flag.
            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                host = args[++i];
            break;
        case "--test-pattern":
            testPattern = true;
            break;
        case "--log":
            logPath = RequireValue(args, ref i, "--log");
            break;
        default:
            if (args[i].StartsWith('-'))
            {
                Console.Error.WriteLine($"Unknown argument: {args[i]}");
                return 1;
            }
            if (input is not null)
            {
                Console.Error.WriteLine($"Unexpected extra argument: {args[i]}");
                return 1;
            }
            input = args[i];
            break;
    }
}

if (testPattern)
{
    if (input is not null)
    {
        Console.Error.WriteLine("--test-pattern generates its own image; don't also pass an input file.");
        return 1;
    }
}
else if (input is null)
{
    Console.Error.WriteLine("No input image given. Pass an image, or use --test-pattern.");
    return 1;
}
else if (!File.Exists(input))
{
    Console.Error.WriteLine($"Input image not found: {input}");
    return 1;
}

output ??= testPattern
    ? $"testpattern_{size}.bin"
    : Path.ChangeExtension(input!, ".bin");

// File log of everything that happens, including any errors.
var (log, logProvider) = FraimicLog.ToFile(logPath);
using (logProvider)
{
    try
    {
        byte[] bin;
        if (testPattern)
        {
            log.LogInformation("Generating orientation test card (frame={Size}, orientation={Orientation}).", size, orientation);
            Console.WriteLine($"Generating orientation test card (frame={size}, orientation={orientation})...");
            bin = FraimicTestPattern.GenerateBin(size, orientation);
        }
        else
        {
            log.LogInformation("Converting {Input} (frame={Size}, orientation={Orientation}, fit={Fit}).", input, size, orientation, fit);
            Console.WriteLine($"Converting {input} (frame={size}, orientation={orientation}, fit={fit})...");
            bin = FraimicConverter.Convert(input!, size, fit: fit, orientation: orientation);
        }
        File.WriteAllBytes(output, bin);
        log.LogInformation("Wrote {Output} ({Bytes} bytes).", output, bin.Length);
        Console.WriteLine($"Wrote {output} ({bin.Length:N0} bytes).");

        if (upload)
        {
            Console.WriteLine($"Uploading to http://{host}/upload (then refreshing) ...");
            await using var device = new FraimicDevice(host, logger: log);
            FraimicResponse result = await device.UploadImageAsync(bin);
            Console.WriteLine($"  HTTP {result.StatusCode}: {result.Body}");
            if (!result.Success)
            {
                Console.Error.WriteLine("Upload failed.");
                return 1;
            }
            Console.WriteLine("Done — uploaded and refresh triggered.");
        }
        Console.WriteLine($"Log: {logPath}");
        return 0;
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Fatal error.");
        Console.Error.WriteLine($"Error: {ex.Message}");
        Console.Error.WriteLine($"See log: {logPath}");
        return 1;
    }
}

static string RequireValue(string[] args, ref int i, string flag)
{
    if (i + 1 >= args.Length)
        throw new ArgumentException($"{flag} requires a value.");
    return args[++i];
}
