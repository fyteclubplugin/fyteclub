using System;
using Dalamud.Plugin;

namespace FyteClub.ModSync.Application
{
    // Penumbra IPC Classes
    internal class GetEnabledState
    {
        private readonly Func<bool> _getEnabledState;

        public GetEnabledState(IDalamudPluginInterface pluginInterface)
        {
            _getEnabledState = pluginInterface.GetIpcSubscriber<bool>("Penumbra.GetEnabledState").InvokeFunc;
        }

        public bool Invoke() => _getEnabledState();
    }
}
