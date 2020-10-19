﻿using GameServerCore;
using GameServerCore.Domain.GameObjects;
using GameServerCore.Packets.Handlers;
using GameServerCore.Packets.PacketDefinitions.Requests;
using System;

namespace LeagueSandbox.GameServer.Packets.PacketHandlers
{
    public class HandleCastSpell : PacketHandlerBase<CastSpellRequest>
    {
        private readonly Game _game;
        private readonly NetworkIdManager _networkIdManager;
        private readonly IPlayerManager _playerManager;

        public HandleCastSpell(Game game)
        {
            _game = game;
            _networkIdManager = game.NetworkIdManager;
            _playerManager = game.PlayerManager;
        }

        public override bool HandlePacket(int userId, CastSpellRequest req)
        {
            Console.WriteLine("{0} is casting {1} {2} {3} {4} {5} {6}", userId, req.SpellSlot, req.TargetNetId, req.X, req.Y, req.X2, req.Y2);

            var targetObj = _game.ObjectManager.GetObjectById(req.TargetNetId);
            var targetUnit = targetObj as IAttackableUnit;
            var owner = _playerManager.GetPeerInfo((ulong)userId).Champion;
            if (owner == null || !owner.CanCast())
            {
                return false;
            }

            var s = owner.GetSpell(req.SpellSlot);
            if (s == null)
            {
                return false;
            }

            return s.Cast(req.X, req.Y, req.X2, req.Y2, targetUnit);
        }
    }
}
