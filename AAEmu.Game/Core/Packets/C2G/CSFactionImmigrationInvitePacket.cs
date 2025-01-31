﻿using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSFactionImmigrationInvitePacket : GamePacket
{
    public CSFactionImmigrationInvitePacket() : base(CSOffsets.CSFactionImmigrationInvitePacket, 5)
    {
    }

    public override void Read(PacketStream stream)
    {
        var invitee = stream.ReadString();

        Logger.Debug("FactionImmigrationInvite, {0}", invitee);
    }
}
