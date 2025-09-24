using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace FyteClub.ModSystem
{
    public class CharacterMonitor : IDisposable
    {
        private readonly IObjectTable _objectTable;
        private readonly IFramework _framework;
        private readonly IPluginLog _pluginLog;
        private readonly Dictionary<ulong, CharacterState> _trackedCharacters = new();
        private bool _disposed;

        public event Action<ICharacter, CharacterChangeType>? CharacterChanged;

        public CharacterMonitor(IObjectTable objectTable, IFramework framework, IPluginLog pluginLog)
        {
            _objectTable = objectTable;
            _framework = framework;
            _pluginLog = pluginLog;
            _framework.Update += OnFrameworkUpdate;
        }

        private unsafe void OnFrameworkUpdate(IFramework framework)
        {
            if (_disposed) return;

            try
            {
                foreach (var obj in _objectTable)
                {
                    if (obj is not ICharacter character || character.Address == IntPtr.Zero) continue;

                    var objectId = character.GameObjectId;
                    var currentState = CreateCharacterState(character);

                    if (_trackedCharacters.TryGetValue(objectId, out var previousState))
                    {
                        if (HasSignificantChange(previousState, currentState))
                        {
                            _trackedCharacters[objectId] = currentState;
                            CharacterChanged?.Invoke(character, DetermineChangeType(previousState, currentState));
                        }
                    }
                    else
                    {
                        _trackedCharacters[objectId] = currentState;
                        CharacterChanged?.Invoke(character, CharacterChangeType.Appeared);
                    }
                }

                // Clean up disappeared characters
                var currentIds = new HashSet<ulong>();
                foreach (var obj in _objectTable)
                {
                    if (obj is ICharacter character) currentIds.Add(character.GameObjectId);
                }

                var toRemove = new List<ulong>();
                foreach (var id in _trackedCharacters.Keys)
                {
                    if (!currentIds.Contains(id)) toRemove.Add(id);
                }
                foreach (var id in toRemove) _trackedCharacters.Remove(id);
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Character monitor error: {ex.Message}");
            }
        }

        private unsafe CharacterState CreateCharacterState(ICharacter character)
        {
            var chara = (Character*)character.Address;
            return new CharacterState
            {
                Name = character.Name.TextValue,
                Position = character.Position,
                EquipHash = CalculateEquipHash(chara),
                CustomizeHash = CalculateCustomizeHash(chara)
            };
        }

        private unsafe uint CalculateEquipHash(Character* chara)
        {
            uint hash = 0;
            for (int i = 0; i < 10; i++)
            {
                hash = hash * 31 + (uint)chara->DrawData.EquipmentModelIds[i].Value;
            }
            return hash;
        }

        private unsafe uint CalculateCustomizeHash(Character* chara)
        {
            uint hash = 0;
            for (int i = 0; i < 26; i++)
            {
                hash = hash * 31 + chara->DrawData.CustomizeData.Data[i];
            }
            return hash;
        }

        private bool HasSignificantChange(CharacterState previous, CharacterState current)
        {
            return previous.EquipHash != current.EquipHash || 
                   previous.CustomizeHash != current.CustomizeHash;
        }

        private CharacterChangeType DetermineChangeType(CharacterState previous, CharacterState current)
        {
            if (previous.EquipHash != current.EquipHash) return CharacterChangeType.Equipment;
            if (previous.CustomizeHash != current.CustomizeHash) return CharacterChangeType.Customize;
            return CharacterChangeType.Other;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _framework.Update -= OnFrameworkUpdate;
            _trackedCharacters.Clear();
        }
    }

    public class CharacterState
    {
        public string Name { get; set; } = "";
        public System.Numerics.Vector3 Position { get; set; }
        public uint EquipHash { get; set; }
        public uint CustomizeHash { get; set; }
    }

    public enum CharacterChangeType
    {
        Appeared,
        Equipment,
        Customize,
        Other
    }
}