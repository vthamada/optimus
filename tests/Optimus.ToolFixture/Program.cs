using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

Console.OutputEncoding = new UTF8Encoding(false);
if (args.Length == 0) return 1;
switch (args[0])
{
    case "echo":
        Console.WriteLine(JsonSerializer.Serialize(args.Skip(1).ToArray()));
        return 0;
    case "environment":
        Console.WriteLine(Environment.GetEnvironmentVariable("OPTIMUS_FIXTURE_VALUE"));
        return 0;
    case "environment-state":
        var selectedValue = Environment.GetEnvironmentVariable(args[1]);
        Console.WriteLine(JsonSerializer.Serialize(new { exists = selectedValue is not null, value = selectedValue }));
        return 0;
    case "wait":
        Console.WriteLine("fixture-started");
        if (args.Length > 1) await File.WriteAllTextAsync(args[1], "fixture-started");
        await Task.Delay(TimeSpan.FromSeconds(30));
        return 0;
    case "flood":
        var buffer = Encoding.UTF8.GetBytes(new string('x', 4096));
        using (var stream = Console.OpenStandardOutput())
            for (int index = 0; index < 2048; index++) await stream.WriteAsync(buffer);
        return 0;
    case "invalid-utf8":
        using (var stream = Console.OpenStandardOutput()) stream.Write([0xff, 0xfe, 0xfd]);
        return 0;
    case "utf16-output":
        using (var stream = Console.OpenStandardOutput()) stream.Write(new UnicodeEncoding(false, false, true).GetBytes("João Ω"));
        using (var stream = Console.OpenStandardError()) stream.Write(new UnicodeEncoding(false, false, true).GetBytes("erro João Ω"));
        return 0;
    case "oem-output":
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var oem = Encoding.GetEncoding(checked((int)FixtureWindows.GetOEMCP()));
        using (var stream = Console.OpenStandardOutput()) stream.Write(oem.GetBytes("João"));
        using (var stream = Console.OpenStandardError()) stream.Write(oem.GetBytes("erro João"));
        return 0;
    case "invalid-utf16":
        using (var stream = Console.OpenStandardOutput()) stream.Write([0x4a, 0x00, 0x6f]);
        return 0;
    case "utf16-limit-boundary":
        // 254 ASCII bytes then a surrogate pair. A 256-byte budget ends after the high surrogate.
        var limitedUtf16 = new UnicodeEncoding(false, false, true).GetBytes(new string('a', 127) + "🙂" + new string('b', 2048));
        using (var stream = Console.OpenStandardOutput()) stream.Write(limitedUtf16);
        return 0;
    case "utf16-both-flood":
        var utf16Encoder = new UnicodeEncoding(false, false, true);
        var outputBlock = utf16Encoder.GetBytes(new string('a', 100));
        var errorBlock = utf16Encoder.GetBytes(new string('b', 100));
        using (var outputStream = Console.OpenStandardOutput())
        using (var errorStream = Console.OpenStandardError())
            for (int index = 0; index < 100; index++)
            {
                outputStream.Write(outputBlock);
                errorStream.Write(errorBlock);
            }
        return 0;
    case "child":
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("wait");
        using (var child = Process.Start(info)) Console.WriteLine($"child:{child!.Id}");
        return 0;
    case "exit":
        Console.Error.WriteLine("fixture stderr");
        return 7;
    default:
        return 2;
}

internal static class FixtureWindows
{
    [DllImport("kernel32.dll")]
    internal static extern uint GetOEMCP();
}
