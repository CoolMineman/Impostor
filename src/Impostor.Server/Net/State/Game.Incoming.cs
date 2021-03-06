﻿using System;
using System.Threading.Tasks;
using Impostor.Server.Games;
using Impostor.Server.Net.Messages;
using Impostor.Shared.Innersloth.Data;
using Impostor.Shared.Innersloth.GameData;

namespace Impostor.Server.Net.State
{
    internal partial class Game
    {
        public async ValueTask HandleStartGame(IMessageReader message)
        {
            GameState = GameStates.Started;

            using var packet = CreateMessage(MessageType.Reliable);
            message.CopyTo(packet);
            await packet.SendToAllAsync();
        }

        public async ValueTask<GameJoinResult> AddClientAsync(IClient client)
        {
            // Check if the IP of the player is banned.
            if (client.Connection != null && _bannedIps.Contains(client.Connection.EndPoint.Address))
            {
                return GameJoinResult.FromError(GameJoinError.Banned);
            }

            var player = client.Player;

            // Check if;
            // - The player is already in this game.
            // - The game is full.
            if (player?.Game != this && _players.Count >= Options.MaxPlayers)
            {
                return GameJoinResult.FromError(GameJoinError.GameFull);
            }

            if (GameState == GameStates.Started)
            {
                return GameJoinResult.FromError(GameJoinError.GameStarted);
            }

            if (GameState == GameStates.Destroyed)
            {
                return GameJoinResult.FromError(GameJoinError.GameDestroyed);
            }

            var isNew = false;

            if (player == null || player.Game != this)
            {
                var clientPlayer = new ClientPlayer(client, this);

                if (!_clientManager.Validate(client))
                {
                    return GameJoinResult.FromError(GameJoinError.InvalidClient);
                }

                isNew = true;
                player = clientPlayer;
                client.Player = clientPlayer;
            }

            // Check current player state.
            if (player.Limbo == LimboStates.NotLimbo)
            {
                return GameJoinResult.FromError(GameJoinError.InvalidLimbo);
            }

            if (GameState == GameStates.Ended)
            {
                await HandleJoinGameNext(player, isNew);
                return GameJoinResult.CreateSuccess(player);
            }

            await HandleJoinGameNew(player, isNew);
            return GameJoinResult.CreateSuccess(player);
        }

        public async ValueTask HandleEndGame(IMessageReader message)
        {
            GameState = GameStates.Ended;

            // Broadcast end of the game.
            using (var packet = CreateMessage(MessageType.Reliable))
            {
                message.CopyTo(packet);
                await packet.SendToAllAsync();
            }

            // Put all players in the correct limbo state.
            foreach (var player in _players)
            {
                player.Value.Limbo = LimboStates.PreSpawn;
            }
        }

        public async ValueTask HandleAlterGame(IMessageReader message, IClientPlayer sender, bool isPublic)
        {
            IsPublic = isPublic;

            using var packet = CreateMessage(MessageType.Reliable);
            message.CopyTo(packet);
            await packet.SendToAllExceptAsync(sender.Client.Id);
        }

        public async ValueTask HandleRemovePlayer(int playerId, DisconnectReason reason)
        {
            await PlayerRemove(playerId);

            // It's possible that the last player was removed, so check if the game is still around.
            if (GameState == GameStates.Destroyed)
            {
                return;
            }

            using var packet = CreateMessage(MessageType.Reliable);
            WriteRemovePlayerMessage(packet, false, playerId, reason);
            await packet.SendToAllExceptAsync(playerId);
        }

        public async ValueTask HandleKickPlayer(int playerId, bool isBan)
        {
            Logger.Information("{0} - Player {1} has left.", Code, playerId);

            using var message = CreateMessage(MessageType.Reliable);

            // Send message to everyone that this player was kicked.
            WriteKickPlayerMessage(message, false, playerId, isBan);
            await message.SendToAllAsync();

            await PlayerRemove(playerId, isBan);

            // Remove the player from everyone's game.
            WriteRemovePlayerMessage(
                message,
                true,
                playerId,
                isBan ? DisconnectReason.Banned : DisconnectReason.Kicked);
            await message.SendToAllExceptAsync(playerId);
        }

        private async ValueTask HandleJoinGameNew(IClientPlayer sender, bool isNew)
        {
            Logger.Information("{0} - Player {1} ({2}) is joining.", Code, sender.Client.Name, sender.Client.Id);

            // Add player to the game.
            if (isNew)
            {
                PlayerAdd(sender);
            }

            using (var message = CreateMessage(MessageType.Reliable))
            {
                WriteJoinedGameMessage(message, false, sender);
                WriteAlterGameMessage(message, false, IsPublic);

                sender.Limbo = LimboStates.NotLimbo;
                await message.SendToAsync(sender);

                await BroadcastJoinMessage(message, true, sender);
            }
        }

        private async ValueTask HandleJoinGameNext(IClientPlayer sender, bool isNew)
        {
            Logger.Information("{0} - Player {1} ({2}) is rejoining.", Code, sender.Client.Name, sender.Client.Id);

            // Add player to the game.
            if (isNew)
            {
                PlayerAdd(sender);
            }

            // Check if the host joined and let everyone join.
            if (sender.Client.Id == HostId)
            {
                GameState = GameStates.NotStarted;

                // Spawn the host.
                await HandleJoinGameNew(sender, false);

                // Pull players out of limbo.
                await CheckLimboPlayers();
                return;
            }

            sender.Limbo = LimboStates.WaitingForHost;

            using var packet = CreateMessage(MessageType.Reliable);

            WriteWaitForHostMessage(packet, false, sender);
            await packet.SendToAsync(sender.Client);

            await BroadcastJoinMessage(packet, true, sender);
        }

        public async ValueTask HandleGameData(IMessageReader parent, IClientPlayer sender, bool toPlayer)
        {
            // Find target player.
            IClientPlayer target = null;

            if (toPlayer)
            {
                var targetId = parent.ReadPackedInt32();
                if (!TryGetPlayer(targetId, out target))
                {
                    // Invalid target.
                    return;
                }
            }

            // Parse GameData messages.
            while (parent.Position < parent.Length)
            {
                var reader = parent.ReadMessage();

                switch (reader.Tag)
                {
                    case GameDataTag.DataFlag:
                    {
                        var objectNetId = reader.ReadPackedUInt32();
                        Logger.Verbose("> Update {0}", objectNetId);
                        break;
                    }

                    case GameDataTag.RpcFlag:
                    {
                        var objectNetId = reader.ReadPackedUInt32();
                        var callId = reader.ReadByte();
                        Logger.Verbose("> RPC {0} {1}", objectNetId, callId);

                        if (callId == 2)
                        {
                            var length = reader.ReadPackedUInt32();
                            var version = reader.ReadByte();
                            var maxPlayers = reader.ReadByte();
                            var language = reader.ReadUInt32();
                            var map = reader.ReadByte();
                            Logger.Verbose("> Map {0}", map);
                        }

                        if (callId == 14)
                        {
                            Logger.Verbose("Start Talking");
                        }
                        if (callId == 23)
                        {
                            Logger.Verbose("End Talking");
                        }

                        break;
                    }

                    case GameDataTag.SpawnFlag:
                    {
                        var objectId = reader.ReadPackedUInt32();
                        Logger.Verbose("> Spawn {0}", objectId);
                        break;
                    }

                    case GameDataTag.DespawnFlag:
                    {
                        var objectNetId = reader.ReadPackedUInt32();
                        Logger.Verbose("> Destroy {0}", objectNetId);
                        break;
                    }

                    case GameDataTag.SceneChangeFlag:
                    {
                        var clientId = reader.ReadPackedInt32();
                        var targetScene = reader.ReadString();
                        Logger.Verbose("> Scene {0} to {1}", clientId, targetScene);
                        break;
                    }

                    case GameDataTag.ReadyFlag:
                    {
                        var clientId = reader.ReadPackedInt32();
                        Logger.Verbose("> IsReady {0}", clientId);
                        break;
                    }

                    default:
                    {
                        Logger.Debug("Bad GameData tag {0}", reader.Tag);
                        break;
                    }
                }
            }
        }
    }
}