using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CitySim.Simulation;

/// <summary>
/// Public transit connection metrics between an origin and destination TAZ from the stop-and-line network search.
/// </summary>
public readonly struct TransitConnectionInfo
{
    public readonly float InVehicleTime;
    public readonly float WaitTime;
    public readonly float CrowdingPenalty;
    public readonly int Transfers;

    /// <summary>True when a valid transit path (direct or multi-transfer) connects the origin and destination TAZs.</summary>
    public bool IsConnected => Transfers >= 0;

    /// <summary>0 = no connection, 1 = direct (0 transfers), 2 = transfer connection (1+ transfers).</summary>
    public int ConnectionType => Transfers < 0 ? 0 : (Transfers == 0 ? 1 : 2);

    /// <summary>In-vehicle time plus any crowding perceived-time penalty.</summary>
    public float PerceivedInVehicleTime => InVehicleTime + CrowdingPenalty;

    public TransitConnectionInfo(float inVehicleTime, float waitTime, int transfers, float crowdingPenalty = 0f)
    {
        InVehicleTime = inVehicleTime;
        WaitTime = waitTime;
        Transfers = transfers;
        CrowdingPenalty = crowdingPenalty;
    }

    public static TransitConnectionInfo Unconnected => new(0f, 0f, -1, 0f);
}

/// <summary>
/// Precomputed public transit travel-time skim across all TAZ pairs, produced by streaming searches on
/// <see cref="TransitNetworkSnapshot"/>. Uses a single 8-byte-per-pair packed array (~17.6 MB at 1 521 TAZs)
/// instead of double-buffered 16-byte structs (~83 MB).
/// </summary>
public sealed class TransitSkim
{
    private const float CrowdingScale = 10.0f; // 0.1 min resolution up to 25.5 min penalty
    private const float InvCrowdingScale = 0.1f;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct PackedEntry
    {
        public float InVehicleTime;
        public Half WaitTime;
        public sbyte Transfers; // -1 = unconnected, 0..126 = number of transfers
        public byte CrowdingTenths;
    }

    private PackedEntry[] _entries = Array.Empty<PackedEntry>();

    /// <summary>Number of active TAZs (rows and columns) in this skim.</summary>
    public int TazCount { get; private set; }

    /// <summary><see cref="RoadGraph.TopologyVersion"/> when the skim was computed; -1 when empty.</summary>
    public int TopologyVersion { get; private set; } = -1;

    /// <summary>Number of transit routes when the skim was computed.</summary>
    public int RouteCount { get; private set; }

    /// <summary>Number of transit stops when the skim was computed.</summary>
    public int StopCount { get; private set; }

    /// <summary>
    /// True when the skim matches the current TAZ count and transit network topology, so it can be read
    /// directly by <see cref="OptimizedODMatrix"/> without an extra pre-OD search round.
    /// </summary>
    public bool Covers(TazManager tazManager, TransitNetworkSnapshot snapshot)
    {
        if (tazManager == null || snapshot == null || tazManager.IsDirty) return false;
        int k = tazManager.ActiveZones.Count;
        return TazCount == k &&
               TopologyVersion == snapshot.TopologyVersion &&
               RouteCount == snapshot.RouteCount &&
               StopCount == snapshot.StopCount;
    }

    /// <summary>Prepares storage for a <paramref name="tazCount"/>×<paramref name="tazCount"/> skim.</summary>
    public void Reset(int tazCount, TransitNetworkSnapshot snapshot)
    {
        TazCount = tazCount;
        TopologyVersion = snapshot?.TopologyVersion ?? -1;
        RouteCount = snapshot?.RouteCount ?? 0;
        StopCount = snapshot?.StopCount ?? 0;

        int required = tazCount * tazCount;
        if (_entries.Length < required)
        {
            _entries = new PackedEntry[Math.Max(required, 16)];
        }

        if (RouteCount == 0 || StopCount == 0)
        {
            ClearAll(required);
        }
    }

    /// <summary>Marks every entry in row <paramref name="originTaz"/> as unconnected.</summary>
    public void ClearRow(int originTaz)
    {
        int k = TazCount;
        int offset = originTaz * k;
        for (int j = 0; j < k; j++)
        {
            _entries[offset + j].Transfers = -1;
        }
    }

    /// <summary>Marks pair (<paramref name="originTaz"/>, <paramref name="destTaz"/>) as unconnected.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetUnconnected(int originTaz, int destTaz)
    {
        _entries[originTaz * TazCount + destTaz].Transfers = -1;
    }

    /// <summary>Writes the transit connection metrics for pair (<paramref name="originTaz"/>, <paramref name="destTaz"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetConnection(int originTaz, int destTaz, float inVehicleTime, float waitTime, int transfers, float crowdingPenalty = 0f)
    {
        int idx = originTaz * TazCount + destTaz;
        int crowdTenths = crowdingPenalty <= 0f ? 0 : Math.Min(255, (int)MathF.Round(crowdingPenalty * CrowdingScale));
        _entries[idx] = new PackedEntry
        {
            InVehicleTime = inVehicleTime,
            WaitTime = (Half)waitTime,
            Transfers = (sbyte)Math.Clamp(transfers, -1, 126),
            CrowdingTenths = (byte)crowdTenths
        };
    }

    /// <summary>Returns the transit connection metrics between two TAZ indices in O(1) time.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TransitConnectionInfo GetConnection(int originTaz, int destTaz)
    {
        int k = TazCount;
        if ((uint)originTaz >= (uint)k || (uint)destTaz >= (uint)k || originTaz == destTaz)
            return TransitConnectionInfo.Unconnected;

        return GetConnectionByIndex(originTaz * k + destTaz);
    }

    /// <summary>Returns the transit connection metrics at linear index <c>originTaz * TazCount + destTaz</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TransitConnectionInfo GetConnectionByIndex(int linearIndex)
    {
        ref readonly PackedEntry e = ref _entries[linearIndex];
        if (e.Transfers < 0) return TransitConnectionInfo.Unconnected;
        return new TransitConnectionInfo(
            e.InVehicleTime,
            (float)e.WaitTime,
            e.Transfers,
            e.CrowdingTenths * InvCrowdingScale);
    }

    private void ClearAll(int count)
    {
        for (int i = 0; i < count; i++)
        {
            _entries[i].Transfers = -1;
        }
    }
}
