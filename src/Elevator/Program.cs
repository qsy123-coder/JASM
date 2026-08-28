using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using WindowsInput;


// Exit codes:
// 0: Success
// 1: Unhandled exception
// 2: Bad arguments

// Commands:
// -2: Alive check
// -1: Exit
// 0: RefreshActiveGenshinMods
// 1: CopyDirectory (<src> and <dst> follow on their own lines; replies "OK" or "FAIL:<msg>")

internal class Program
{
    public static void Main(string[] args)
    {
        var userName = "";
        try
        {
            userName = args.First();
        }
        catch
        {
            Console.Error.WriteLine("Please provide a username");
            Environment.Exit(2);
        }

        try
        {
            StartPipeServer(userName);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            Environment.Exit(1);
        }
    }


    static void StartPipeServer(string userName)
    {
        var specificUserAccount = new NTAccount(userName);
        var specificUserSid = (SecurityIdentifier)specificUserAccount.Translate(typeof(SecurityIdentifier));

        var ps = new PipeSecurity();

        var userAccessRule = new PipeAccessRule(specificUserSid,
            PipeAccessRights.FullControl, AccessControlType.Allow);
        ps.AddAccessRule(userAccessRule);

        while (true)
        {
            // InOut so we can reply to commands that need a result (e.g. CopyDirectory).
            using var pipeServer = NamedPipeServerStreamConstructors.New("MyPipess", PipeDirection.InOut, 1,
                PipeTransmissionMode.Message,
                PipeOptions.Asynchronous, pipeSecurity: ps);
            Console.WriteLine("Waiting for connection...");

            pipeServer.WaitForConnection();
            Console.WriteLine("Connected!");
            Console.WriteLine("----------------------");


            using var reader = new StreamReader(pipeServer);
            var command = reader.ReadLine();
            Console.WriteLine("Received command: " + command);
            Console.WriteLine("From user: " + pipeServer.GetImpersonationUserName());

            switch (command)
            {
                case "-2":
                    break;
                case "-1":
                    Console.WriteLine("Exiting");
                    Environment.Exit(0);
                    return;
                case "0":
                    Console.WriteLine("Refreshing Genshin Mods");
                    RefreshGenshinMods();
                    break;
                case "1":
                    Console.WriteLine("Copying directory");
                    HandleCopyCommand(pipeServer, reader);
                    break;

                default:
                    Console.Error.WriteLine($"Unknown command: {command}");
                    break;
            }
        }
    }

    /// <summary>
    /// Reads &lt;src&gt; and &lt;dst&gt; lines, copies the directory tree (overwriting) and replies
    /// "OK" or "FAIL:&lt;message&gt;" so the caller knows the elevated copy succeeded.
    /// </summary>
    static void HandleCopyCommand(PipeStream pipe, StreamReader reader)
    {
        using var writer = new StreamWriter(pipe) { AutoFlush = true };

        var src = reader.ReadLine();
        var dst = reader.ReadLine();

        if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(dst))
        {
            writer.WriteLine("FAIL:Missing source or destination path");
            return;
        }

        try
        {
            DirectoryCopy(src, dst, overwrite: true);
            Console.WriteLine($"Copied {src} -> {dst}");
            writer.WriteLine("OK");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            writer.WriteLine("FAIL:" + ex.Message);
        }
    }

    static void DirectoryCopy(string sourceDir, string destDir, bool overwrite)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            DirectoryCopy(subDir, Path.Combine(destDir, Path.GetFileName(subDir)), overwrite);
        }
    }

    [DllImport("User32.dll")]
    static extern int SetForegroundWindow(IntPtr point);


    static void RefreshGenshinMods()
    {
        var ptr = GetGenshinProcess();

        if (ptr == null) return;


        _ = SetForegroundWindow(ptr.Value);

        new InputSimulator().Keyboard
            .KeyDown(VirtualKeyCode.F10)
            .Sleep(100)
            .KeyUp(VirtualKeyCode.F10)
            .Sleep(100);
    }


    static IntPtr? GetGenshinProcess()
    {
        var processes = Process.GetProcessesByName("GenshinImpact");

        foreach (var process in processes)
        {
            Console.WriteLine("Title: " + process.MainWindowTitle);
        }

        if (processes.Length > 1)
        {
            Console.Error.WriteLine("Multiple GenshinImpact.exe processes found");
            return null;
        }

        var ptr = processes.FirstOrDefault()?.MainWindowHandle;
        if (ptr == IntPtr.Zero)
        {
            Console.Error.WriteLine("GenshinImpact.exe process not found");
            return null;
        }

        return ptr;
    }
}

/*[DllImport("user32.dll")]
static extern bool PostMessage(IntPtr hWnd, UInt32 Msg, int wParam, int lParam);

const UInt32 WM_KEYDOWN = 0x0100;
const int VK_F10 = 0x79;

async Task RefreshGenshinMods()
{
    var ptr = GetGenshinProcess().MainWindowHandle;


    SetForegroundWindow(ptr);
    await Task.Delay(100);

    var success = PostMessage(ptr, WM_KEYDOWN, VK_F10, 0);

    Console.WriteLine(!success ? "Failed to send message" : "Sent message");
}*/

/*async Task RefreshGenshinModsWinInput()
{
    var ptr = GetGenshinProcess().MainWindowHandle;

    SetForegroundWindow(ptr);
    await Task.Delay(1000);

    await WindowsInput.Simulate.Events()
        .Click(KeyCode.F10)
        .Invoke();
}*/