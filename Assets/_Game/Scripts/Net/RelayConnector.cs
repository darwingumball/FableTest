using System;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;

namespace Game.Net
{
    /// <summary>
    /// Points UnityTransport at a Relay allocation so host and clients talk through Relay
    /// (no port forwarding). DTLS everywhere.
    /// </summary>
    public static class RelayConnector
    {
        private const string CONNECTION_TYPE = "dtls";

        /// <summary>Host: allocate relay for maxPlayers-1 remote clients, return the join code for the lobby.</summary>
        public static async Task<string> HostAllocateAsync(int maxPlayers)
        {
            var utp = GetUnityTransport();
            int maxConnections = Mathf.Max(1, maxPlayers - 1);
            Allocation allocation = await RelayService.Instance.CreateAllocationAsync(maxConnections);
            string joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
            utp.SetRelayServerData(allocation.ToRelayServerData(CONNECTION_TYPE));
            return joinCode;
        }

        /// <summary>Client: join relay by code and configure UnityTransport.</summary>
        public static async Task ClientJoinAsync(string joinCode)
        {
            if (string.IsNullOrEmpty(joinCode))
                throw new ArgumentException("Relay join code is empty.", nameof(joinCode));
            var utp = GetUnityTransport();
            JoinAllocation join = await RelayService.Instance.JoinAllocationAsync(joinCode);
            utp.SetRelayServerData(join.ToRelayServerData(CONNECTION_TYPE));
        }

        private static UnityTransport GetUnityTransport()
        {
            if (NetworkManager.Singleton == null)
                throw new InvalidOperationException("NetworkManager.Singleton is null - instantiate the NetworkManager prefab first.");
            if (NetworkManager.Singleton.NetworkConfig.NetworkTransport is not UnityTransport utp)
                throw new InvalidOperationException("NetworkManager's transport is not UnityTransport.");
            return utp;
        }
    }
}
