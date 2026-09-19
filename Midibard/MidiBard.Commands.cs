using System;
using System.Linq;

using MidiBard.Control.CharacterControl;
using MidiBard.Control.MidiControl;

using static Dalamud.api;

namespace MidiBard;

public partial class MidiBard
{
    [Command("/midibard")]
    [HelpMessage("Toggle MidiBard window")]
    public void Command1(string command, string args) => OnCommand(command, args);

    [Command("/mbard")]
    [HelpMessage("Toggle MidiBard window\n")]
    public void OnCommand(string command, string args)
    {
        var argStrings = args.ToLowerInvariant()
            .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        api.PluginLog.Debug($"command: {command}, {string.Join('|', argStrings)}");

        if (!argStrings.Any())
        {
            Ui.ToggleMainWindow();
            return;
        }

        switch (argStrings[0])
        {
            case "cancel":
                PerformActions.DoPerformActionOnTick(0);
                break;

            case "perform":
                HandlePerformCommand(argStrings, args);
                break;

            case "playpause":
                MidiPlayerControl.PlayPause();
                break;

            case "play":
                MidiPlayerControl.Play();
                break;

            case "pause":
                MidiPlayerControl.Pause();
                break;

            case "stop":
                MidiPlayerControl.Stop();
                break;

            case "next":
                MidiPlayerControl.Next();
                break;

            case "prev":
                MidiPlayerControl.Prev();
                break;

            case "visual":
                HandleVisualCommand(argStrings);
                break;

            case "rewind":
                HandleRewindCommand(argStrings);
                break;

            case "fastforward":
                HandleFastForwardCommand(argStrings);
                break;

            case "transpose":
                HandleTransposeCommand(argStrings);
                break;
        }
    }

    private static void HandlePerformCommand(System.Collections.Generic.List<string> argStrings, string originalArgs)
    {
        try
        {
            var instrumentInput = argStrings[1];
            if (instrumentInput == "cancel")
            {
                PerformActions.DoPerformActionOnTick(0);
            }
            else if (uint.TryParse(instrumentInput, out var id1) && id1 < InstrumentStrings.Length)
            {
                SwitchInstrument.SwitchToContinue(id1);
            }
            else if (SwitchInstrument.TryParseInstrumentName(instrumentInput, out var id2))
            {
                SwitchInstrument.SwitchToContinue(id2);
            }
        }
        catch (Exception e)
        {
            PluginLog.Warning(e, "error when parsing or finding instrument strings");
            api.ChatGui.PrintError($"failed parsing command argument \"{originalArgs}\"");
        }
    }

    private static void HandleVisualCommand(System.Collections.Generic.List<string> argStrings)
    {
        try
        {
            switch (argStrings[1])
            {
                case "on":
                    Ui.OpenTrackVisualizerWindow();
                    break;
                case "off":
                    Ui.CloseTrackVisualizerWindow();
                    break;
            }
        }
        catch
        {
            Ui.CloseTrackVisualizerWindow();
        }
    }

    private static void HandleRewindCommand(System.Collections.Generic.List<string> argStrings)
    {
        double timeInSeconds = -5;
        try
        {
            timeInSeconds = -double.Parse(argStrings[1]);
        }
        catch { }

        MidiPlayerControl.MoveTime(timeInSeconds);
    }

    private static void HandleFastForwardCommand(System.Collections.Generic.List<string> argStrings)
    {
        double timeInSeconds = 5;
        try
        {
            timeInSeconds = double.Parse(argStrings[1]);
        }
        catch { }

        MidiPlayerControl.MoveTime(timeInSeconds);
    }

    private static void HandleTransposeCommand(System.Collections.Generic.List<string> argStrings)
    {
        try
        {
            if (argStrings[1] == "set")
            {
                config.TransposeGlobal = int.Parse(argStrings[2]);
            }
            else
            {
                config.TransposeGlobal += int.Parse(argStrings[1]);
            }
        }
        catch { }
    }
}
