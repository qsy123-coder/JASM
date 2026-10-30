using System.ComponentModel;
using Windows.Win32;
using Windows.Win32.Foundation;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.Core.Services.CommandService;
using GIMI_ModManager.Core.Services.CommandService.Models;
using GIMI_ModManager.WinUI.Services.Injection;
using GIMI_ModManager.WinUI.Services.ModHandling;
using GIMI_ModManager.WinUI.Services.Notifications;
using Serilog;

namespace GIMI_ModManager.WinUI.Services;

public class CommandHandlerService(CommandService commandService, GameLaunchInjectionService injectionService,
    ILogger logger)
{
    private readonly ILogger _logger = logger.ForContext<CommandHandlerService>();
    private readonly CommandService _commandService = commandService;
    private readonly GameLaunchInjectionService _injectionService = injectionService;


    public async Task<ICollection<string>> CanRunCommandAsync(Guid commandId, SpecialVariablesInput? variablesInput,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await InternalCanRunCommandAsync(commandId, variablesInput, cancellationToken);
        }
        catch (Exception e)
        {
#if DEBUG
            throw;
#endif

            _logger.Error(e, "An error occured when checking if command can be started");
            return new List<string> { $"An error occured when checking if command can be started: {e.Message}" };
        }
    }

    private async Task<ICollection<string>> InternalCanRunCommandAsync(Guid commandId,
        SpecialVariablesInput? variablesInput,
        CancellationToken cancellationToken = default)
    {
        var command = await _commandService.GetCommandDefinitionAsync(commandId, cancellationToken: cancellationToken);

        if (command is null)
        {
            return [$"Command with id '{commandId}' not found"];
        }


        var errors = new List<string>();

        if (command.ExecutionOptions.Command.IsNullOrEmpty())
        {
            errors.Add("Executable path value is empty null or empty");
        }


        if (!File.Exists(command.ExecutionOptions.Command) && !IsExeFoundInPath(command))
        {
            errors.Add(
                $"Executable '{command.ExecutionOptions.Command}' not found in $PATH or Executable file does not exist");
        }


        if (command.ExecutionOptions.WorkingDirectory != SpecialVariables.TargetPath &&
            command.ExecutionOptions.WorkingDirectory is not null &&
            !Directory.Exists(command.ExecutionOptions.WorkingDirectory))
        {
            errors.Add(
                $"Working directory '{command.ExecutionOptions.WorkingDirectory}' does not exist");
        }


        // This check is a bit messy but TargetPath is the only special variable that exists for now
        if (command.ExecutionOptions.HasAnySpecialVariables())
        {
            if (variablesInput is null)
            {
                errors.Add($"Special variables are required for this command");
            }
            else
            {
                var strings = new[]
                {
                    command.ExecutionOptions.Command, command.ExecutionOptions.WorkingDirectory,
                    command.ExecutionOptions.Arguments
                };

                if (strings.Any(x => x is not null && x.Contains(SpecialVariables.TargetPath)) &&
                    !variablesInput.HasSpecialVariable(SpecialVariables.TargetPath))
                {
                    errors.Add($"Special variable '{SpecialVariables.TargetPath}' is required for this command");
                }
            }
        }

        var usesTargetPath = command.ExecutionOptions.HasAnySpecialVariables([SpecialVariables.TargetPath]);

        if (usesTargetPath &&
            (variablesInput is null || !variablesInput.HasSpecialVariable(SpecialVariables.TargetPath)))
        {
            errors.Add($"Special variable '{SpecialVariables.TargetPath}' is required for this command");
        }

        if (usesTargetPath)
        {
            var targetPath = variablesInput?.GetVariable(SpecialVariables.TargetPath);

            if (targetPath is not null && !Directory.Exists(targetPath))
            {
                errors.Add($"Target path '{targetPath}' does not exist");
            }
        }

        return errors;
    }

    public async Task<Result<ProcessCommand>> RunCommandAsync(Guid commandId, SpecialVariablesInput? variablesInput,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await InternalRunCommandAsync(commandId, variablesInput, cancellationToken);
        }
        catch (Exception e)
        {
#if DEBUG
            if (e is not Win32Exception)
                throw;
#endif

            _logger.Error(e, "An error occured when starting command");
            return Result<ProcessCommand>.Error(e, new SimpleNotification("An error occured when starting command",
                e.Message, null));
        }
    }


    private async Task<Result<ProcessCommand>> InternalRunCommandAsync(Guid commandId,
        SpecialVariablesInput? variablesInput,
        CancellationToken cancellationToken)
    {
        var command = await _commandService.GetCommandDefinitionAsync(commandId, cancellationToken: cancellationToken);

        if (command is null)
        {
            return Result<ProcessCommand>.Error(new SimpleNotification("Command not found",
                $"Command with id '{commandId}' not found"));
        }

        var errors = await InternalCanRunCommandAsync(commandId, variablesInput, cancellationToken);

        if (errors.Count > 0)
        {
            return Result<ProcessCommand>.Error(new SimpleNotification("Command cannot be started",
                $"Command '{command.CommandDisplayName}' cannot be started due to the following errors: {string.Join(", ", errors)}"));
        }

        var processCommand = _commandService.CreateCommand(command, variablesInput);

        // 鸣潮 game-start 命令：开关打开时尝试「挂起直启 + 注入 WWMI d3d11」；
        // 任何失败都回退到原来的 XXMI Launcher 命令，绝不因为新路径失败就禁止启动游戏。
        if (command.IsGameStartCommand && await _injectionService.IsEnabledAsync(cancellationToken))
        {
            var injection = await _injectionService.TryLaunchAsync(cancellationToken);
            if (injection.Success)
            {
                var attached = _commandService.AttachToRunningProcess(command, injection.ProcessId, variablesInput);
                if (attached is not null)
                {
                    _logger.Information("[注入启动] 已启动并附着 pid={Pid}", injection.ProcessId);
                    return Result<ProcessCommand>.Success(attached,
                        new SimpleNotification("Command started",
                            $"Command '{command.CommandDisplayName}' started (injected)"));
                }

                // 游戏已经起来了，不能再回退（否则会双开）；只报无法监控。
                _logger.Warning("[注入启动] 游戏已启动 pid={Pid}，但无法附着进程，跳过回退", injection.ProcessId);
                return Result<ProcessCommand>.Error(new SimpleNotification("Game started but not monitored",
                    $"Game started (pid {injection.ProcessId}) but could not be attached for monitoring."));
            }

            _logger.Warning("[注入启动] 失败，回退原启动命令：{Reason} {Error}", injection.Reason, injection.Error);
        }

        processCommand.Start();


        return Result<ProcessCommand>.Success(processCommand, new SimpleNotification("Command started",
            $"Command '{command.CommandDisplayName}' started"));
    }

    public async Task<List<CommandDefinition>> GetCommandsThatContainSpecialVariablesAsync(
        params string[] specialVariable)
    {
        if (!specialVariable.Where(s => !s.IsNullOrEmpty()).Any(s => SpecialVariables.AllVariables.Contains(s)))
            return [];


        var commands = await _commandService.GetCommandDefinitionsAsync();

        return commands.Where(x => x.ExecutionOptions.HasAnySpecialVariables(specialVariable)).ToList();
    }

    private unsafe bool IsExeFoundInPath(CommandDefinition commandDefinition)
    {
        var index = 0;
        var charBuffer = new Span<char>(new char[500]);

        var command = commandDefinition.ExecutionOptions.Command;

        command = command.EndsWith(".exe") ? command : command + ".exe";

        foreach (var c in command.AsEnumerable().Append('\0'))
        {
            charBuffer[index] = c;
            index++;
        }

        if (!charBuffer.IsEmpty && charBuffer.LastIndexOf('\0') == -1)
            throw new ArgumentException("Required null terminator missing.");

        fixed (char* p = charBuffer)
        {
            var result = PInvoke.PathFindOnPath(new PWSTR(p));

            return result;
        }
    }

    public async Task<IEnumerable<ProcessCommand>> GetRunningCommandAsync(Guid commandDefinitionId)
    {
        return (await _commandService.GetRunningCommandsAsync().ConfigureAwait(false))
            .Where(x => x.CommandDefinitionId == commandDefinitionId)
            .ToArray();
    }
}