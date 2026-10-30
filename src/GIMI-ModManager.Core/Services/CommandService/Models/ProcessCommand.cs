using System.Diagnostics;
using GIMI_ModManager.Core.Helpers;

namespace GIMI_ModManager.Core.Services.CommandService.Models;

/// <summary>
/// This is returned when a command is created, is used to track and interact with the process
/// </summary>
public sealed class ProcessCommand
{
    private Process _process;
    private readonly CommandContext _context;

    internal InternalCreateCommandOptions InternalCreateOptions { get; }
    public CommandExecutionOptions Options => InternalCreateOptions.ExecutionOptions;

    public Guid RunId => _context.RunId;

    public Guid CommandDefinitionId => InternalCreateOptions.CommandDefinitionId;

    public string DisplayName => InternalCreateOptions.DisplayName;

    public string FullCommand => _process.StartInfo.FileName + " " + _process.StartInfo.Arguments;

    internal ProcessCommand(Process process, InternalCreateCommandOptions options,
        CommandContext context)
    {
        _process = process;
        InternalCreateOptions = options;
        _context = context;

        CanWriteInputReadOutput = InternalCreateOptions.ExecutionOptions.CanRedirectInputOutput();
    }

    public bool HasBeenStarted { get; private set; }
    public bool CanWriteInputReadOutput { get; private set; }

    public bool HasExited { get; private set; }

    public bool IsRunning => !HasExited;

    public int ExitCode { get; private set; } = 1337;

    public event EventHandler? Started;
    public event EventHandler? Exited;
    public event EventHandler<DataReceivedEventArgs>? OutputDataReceived;
    public event EventHandler<DataReceivedEventArgs>? ErrorDataReceived;


    public bool Start()
    {
        if (HasBeenStarted)
            throw new InvalidOperationException("Cannot start a command that has already been started");

        _process.Exited += ExitHandler;


        var currentWorkingDirectory = Environment.CurrentDirectory;

        if ((Options.UseShellExecute || Options.RunAsAdmin) && !_process.StartInfo.WorkingDirectory.IsNullOrEmpty())
        {
            if (Directory.Exists(_process.StartInfo.WorkingDirectory))
            {
                Environment.CurrentDirectory = _process.StartInfo.WorkingDirectory;
            }
            else
            {
                throw new InvalidOperationException(
                    "Working directory does not exist for a command using shell execute");
            }
        }

        bool result;
        try
        {
            result = _process.Start();
        }
        catch (Exception e)
        {
            _process.Exited -= ExitHandler;
            throw;
        }
        finally
        {
            Environment.CurrentDirectory = currentWorkingDirectory;
        }

        HasBeenStarted = true;
        _context.StartTime = DateTime.Now;

        if (CanWriteInputReadOutput)
        {
            _process.OutputDataReceived += (_, args) => OutputDataReceived?.Invoke(this, args);
            _process.ErrorDataReceived += (_, args) => ErrorDataReceived?.Invoke(this, args);

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        Started?.Invoke(this, EventArgs.Empty);

        _process.Refresh();
        return result;
    }

    /// <summary>
    /// 附着到一个已经在运行的外部进程（提权助手挂起创建并注入后回传的 PID）。
    ///
    /// 这条路径不经过 <see cref="Start"/>：进程不是本对象创建的，没有可用的 StartInfo/重定向，
    /// 但仍要挂上 <c>Exited</c>、标记 Started，让 <see cref="CommandService"/> 的运行中命令表与
    /// 进程管理器看到同一份状态。
    /// </summary>
    internal void AttachToRunningProcess(Process process)
    {
        if (HasBeenStarted)
            throw new InvalidOperationException("Cannot attach a command that has already been started");

        // 构造时创建的占位 Process 没有启动过，直接换掉
        _process.Dispose();
        _process = process;
        _process.Exited += ExitHandler;
        _process.EnableRaisingEvents = true;

        CanWriteInputReadOutput = false;
        HasBeenStarted = true;
        _context.StartTime = DateTime.Now;
        Started?.Invoke(this, EventArgs.Empty);

        _process.Refresh();
        if (_process.HasExited)
            ExitHandler(this, EventArgs.Empty);
    }

    private void ExitHandler(object? sender, EventArgs args)
    {
        HasExited = true;
        ExitCode = _process.ExitCode;
        _context.EndTime = DateTime.Now;
        _process.Dispose();
        Exited?.Invoke(this, args);
    }

    public async Task WaitForExitAsync()
    {
        if (HasExited)
            return;

        // TODO: Use task completion source to wait for the process to exit
        await Task.Run(() => _process.WaitForExit()).ConfigureAwait(false);
    }


    public async Task WriteInputAsync(string? input, bool appendNewLine = true)
    {
        if (!CanWriteInputReadOutput)
            throw new InvalidOperationException("Cannot write input to a process that uses shell execute");

        if (HasExited)
            throw new InvalidOperationException("Cannot write input to a process that has exited");

        var inputToWrite = input + (appendNewLine ? Environment.NewLine : string.Empty);

        await _process.StandardInput.WriteAsync(inputToWrite).ConfigureAwait(false);
    }


    public async Task<int> KillAsync()
    {
        if (HasExited)
            return ExitCode;

        _process.Kill();
        await WaitForExitAsync().ConfigureAwait(false);
        return ExitCode;
    }
}