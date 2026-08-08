using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Plugin.Services;
using FyteClub.Core;
using FyteClub.Core.Logging;

namespace FyteClub.UI
{
    /// <summary>
    /// Draws a small floating progress bar above a player's head while their mods are being
    /// received, so a sync in progress is visible in-world instead of only in dalamud.log.
    /// </summary>
    public class TransferProgressOverlay
    {
        private readonly FyteClubPlugin _plugin;
        private readonly IObjectTable _objectTable;
        private readonly IGameGui _gameGui;
        // Edge-tracking only, so this logs once per state change instead of once per frame
        // (this runs every ImGui frame - unconditional per-frame logging would flood dalamud.log).
        private string? _lastLoggedState;

        private const float BarWidth = 160f;
        private const float BarHeight = 18f;
        private const float HeightAboveHead = 2.2f;

        public TransferProgressOverlay(FyteClubPlugin plugin, IObjectTable objectTable, IGameGui gameGui)
        {
            _plugin = plugin;
            _objectTable = objectTable;
            _gameGui = gameGui;
        }

        private void LogStateChange(string state)
        {
            if (_lastLoggedState == state)
            {
                return;
            }
            _lastLoggedState = state;
            FyteLog.Info(LogModule.UI, "[TransferProgressOverlay] {0}", state);
        }

        public void Draw()
        {
            // Two live tests showed provably-active transfer data in the orchestrator (MOD
            // COMPLETION log lines incrementing normally) while this method logged zero state
            // changes for the entire transfer window - meaning either GetActiveTransferProgress()
            // never returned non-null here, or something after that throws before reaching the
            // log calls below. A try/catch around the whole body rules out the second case:
            // if Dalamud is silently swallowing an exception from this Draw handler, this makes
            // it show up in dalamud.log instead of vanishing.
            try
            {
                DrawInternal();
            }
            catch (Exception ex)
            {
                LogStateChange($"EXCEPTION: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void DrawInternal()
        {
            var progress = _plugin.GetActiveTransferProgress();
            if (progress == null)
            {
                LogStateChange("No active transfer");
                return;
            }

            var character = FindCharacterByName(progress.Value.PlayerName);
            if (character == null)
            {
                LogStateChange($"Progress active for {progress.Value.PlayerName} ({progress.Value.FilesCompleted}/{progress.Value.FilesTotal}) but character not found in object table - not drawing");
                return;
            }

            var worldPos = character.Position with { Y = character.Position.Y + HeightAboveHead };
            if (!_gameGui.WorldToScreen(worldPos, out var screenPos))
            {
                LogStateChange($"Progress active for {progress.Value.PlayerName} ({progress.Value.FilesCompleted}/{progress.Value.FilesTotal}), character found, but WorldToScreen failed - off-screen or behind camera");
                return;
            }

            LogStateChange($"Drawing bar for {progress.Value.PlayerName} at screen ({screenPos.X:F0},{screenPos.Y:F0})");

            var topLeft = new Vector2(screenPos.X - BarWidth / 2, screenPos.Y - BarHeight / 2);
            var bottomRight = topLeft + new Vector2(BarWidth, BarHeight);
            var fillWidth = BarWidth * Math.Clamp(progress.Value.Fraction, 0f, 1f);

            var drawList = ImGui.GetForegroundDrawList();
            drawList.AddRectFilled(topLeft, bottomRight, ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0.65f)), 4f);
            drawList.AddRectFilled(topLeft, topLeft + new Vector2(fillWidth, BarHeight), ImGui.ColorConvertFloat4ToU32(new Vector4(0.25f, 0.75f, 0.35f, 0.9f)), 4f);
            drawList.AddRect(topLeft, bottomRight, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.8f)), 4f);

            var label = $"Syncing mods {progress.Value.FilesCompleted}/{progress.Value.FilesTotal}";
            var textSize = ImGui.CalcTextSize(label);
            var textPos = new Vector2(screenPos.X - textSize.X / 2, topLeft.Y - textSize.Y - 2);
            drawList.AddText(textPos + Vector2.One, ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 1)), label);
            drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 1)), label);
        }

        private Dalamud.Game.ClientState.Objects.Types.ICharacter? FindCharacterByName(string characterName)
        {
            var cleanName = characterName.Contains('@') ? characterName.Split('@')[0] : characterName;

            foreach (var obj in _objectTable)
            {
                if (obj.Name?.TextValue != null &&
                    obj.Name.TextValue.Equals(cleanName, StringComparison.OrdinalIgnoreCase) &&
                    obj is Dalamud.Game.ClientState.Objects.Types.ICharacter character)
                {
                    return character;
                }
            }

            return null;
        }
    }
}
