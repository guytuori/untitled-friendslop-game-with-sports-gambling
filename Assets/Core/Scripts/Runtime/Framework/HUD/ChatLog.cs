using System;
using System.Collections.Generic;
using System.Text;
using Fusion;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>One line in the chat, as this player sees it.</summary>
    public class ChatLine
    {
        /// <summary>When it arrived here, "HH:mm:ss" (this machine's clock - it's sent and received within moments).</summary>
        public string Time;
        public int SenderId;
        /// <summary>The sender's name as this player sees it (Show Display Names, Mature Language Filter), taken on arrival.</summary>
        public string SenderName;
        /// <summary>The message itself, in this player's language (builder / set phrases) or as typed (filtered).</summary>
        public string Body;
        public bool Typed;
    }

    /// <summary>
    /// In-game chat messages (2026-11-10): sending, receiving and the list this player sees. Drawn and typed into
    /// by <see cref="ChatOverlay"/>.
    ///
    /// THREE KINDS of message go over the network (static Fusion RPCs on <see cref="ChatSync"/>, like the profile
    /// and ready-up RPCs - they work in the lobby, between rounds and in the match alike):
    ///   - typed text: up to <see cref="MaxLength"/> characters, sent in pieces so no RPC goes over Fusion's
    ///     512-byte limit, put back together on arrival. Each player's Mature Language Filter masks bad words
    ///     (NameFilter, the same filter as names). Dropped on arrival in a Message Builder Chat Only game, or if
    ///     this player has Message Builder Chat Only on;
    ///   - a built sentence ("Player 2 should climb a wall soon."): only the ids are sent; each player's game
    ///     words it in their own language (<see cref="ChatPhrases"/>);
    ///   - a set phrase ("Good job!"): its id.
    /// Static RPCs don't come back to the sender, so the sender adds its own message straight away.
    ///
    /// ANTI-SPAM: a <see cref="SendCooldown"/> second gap between this player's messages - enough to stop pasting
    /// and mashing Enter, short enough not to get in the way of sending a few words at a time.
    ///
    /// Cleared when each round starts (and when a session starts or ends); kept in memory only.
    /// </summary>
    public static class ChatLog
    {
        public const int MaxLength = 255;
        public const float SendCooldown = 0.33f;
        public const int MaxLines = 60;

        /// <summary>Characters per network piece - at most 3 bytes each in UTF-8, so a piece stays well under 512 bytes.</summary>
        private const int ChunkLength = 100;
        private const int MaxChunks = (MaxLength + ChunkLength - 1) / ChunkLength;

        private static readonly List<ChatLine> s_Lines = new List<ChatLine>();
        private static readonly Dictionary<long, string[]> s_Partial = new Dictionary<long, string[]>();
        private static int s_Sequence;
        private static float s_LastSendTime = -10f;
        private static NetworkRunner s_TiltSentIn;

        /// <summary>Raised when a line is added (on this machine).</summary>
        public static event Action<ChatLine> Added;

        /// <summary>Raised when the chat is wiped (a new round, or the session changed).</summary>
        public static event Action Cleared;

        public static IReadOnlyList<ChatLine> Lines => s_Lines;

        private static NetworkRunner Runner => FusionSessionService.HasInstance ? FusionSessionService.Instance.GameRunner : null;

        public static void Clear()
        {
            s_Lines.Clear();
            s_Partial.Clear();
            Cleared?.Invoke();
        }

        /// <summary>True once the cooldown since this player's last message has passed.</summary>
        public static bool CanSendNow => UnityEngine.Time.unscaledTime - s_LastSendTime >= SendCooldown;

        /// <summary>The host made this a Message Builder Chat Only game (their Gameplay setting when they hosted it).</summary>
        public static bool SessionIsBuilderOnly
        {
            get
            {
                if (!FusionSessionService.HasInstance || !FusionSessionService.Instance.InGame) return false;
                return FusionSessionService.Instance.SessionRules.MessageBuilderOnly;
            }
        }

        /// <summary>Whether this player may type messages here (otherwise it's the message builder for everything).</summary>
        public static bool TypingAllowed => !SessionIsBuilderOnly && !GameplaySettings.Current.MessageBuilderChatOnly;

        /// <summary>"I'm using tilt controls!" goes once per game.</summary>
        public static bool TiltControlsSent => s_TiltSentIn != null && s_TiltSentIn == Runner;

        // =====================================================================================
        // Sending
        // =====================================================================================

        /// <summary>Sends a typed message. False if there's nothing to send, the cooldown hasn't passed or there's no session.</summary>
        public static bool SendText(string text)
        {
            text = Clean(text);
            NetworkRunner runner = Runner;
            if (text.Length == 0 || !CanSendNow || !TypingAllowed || !IsLive(runner)) return false;

            s_LastSendTime = UnityEngine.Time.unscaledTime;
            int sequence = ++s_Sequence;
            int parts = (text.Length + ChunkLength - 1) / ChunkLength;
            for (int i = 0, at = 0; i < parts; i++)
            {
                int length = Mathf.Min(ChunkLength, text.Length - at);
                // Don't split a surrogate pair (an emoji) across two pieces.
                if (at + length < text.Length && char.IsHighSurrogate(text[at + length - 1])) length--;
                if (i == parts - 1) length = text.Length - at;
                ChatSync.TextRpc(runner, sequence, i, parts, text.Substring(at, length));
                at += length;
            }

            AddLine(runner.LocalPlayer.PlayerId, NameFilter.ForDisplay(text), typed: true);
            return true;
        }

        /// <summary>Sends "[target] should / should not [action] [when]".</summary>
        public static bool SendBuilder(int targetId, bool should, int action, int when)
        {
            NetworkRunner runner = Runner;
            if (!ChatPhrases.IsValidAction(action) || !ChatPhrases.IsValidWhen(when) || !CanSendNow || !IsLive(runner)) return false;

            s_LastSendTime = UnityEngine.Time.unscaledTime;
            ChatSync.BuilderRpc(runner, targetId, should, action, when);
            AddBuilderLine(runner.LocalPlayer.PlayerId, targetId, should, action, when);
            return true;
        }

        /// <summary>Sends a set phrase.</summary>
        public static bool SendPreset(int preset)
        {
            NetworkRunner runner = Runner;
            if (!ChatPhrases.IsValidPreset(preset) || !CanSendNow || !IsLive(runner)) return false;
            if (preset == ChatPhrases.PresetTiltControls)
            {
                if (TiltControlsSent) return false;
                s_TiltSentIn = runner;
            }

            s_LastSendTime = UnityEngine.Time.unscaledTime;
            ChatSync.PresetRpc(runner, preset);
            AddLine(runner.LocalPlayer.PlayerId, ChatPhrases.Preset(preset), typed: false);
            return true;
        }

        private static bool IsLive(NetworkRunner runner) => runner != null && runner.IsRunning && runner.LocalPlayer.IsRealPlayer;

        // =====================================================================================
        // Receiving (ChatSync)
        // =====================================================================================

        internal static void ReceiveTextPart(int senderId, int sequence, int part, int parts, string chunk)
        {
            if (parts < 1 || parts > MaxChunks || part < 0 || part >= parts || chunk == null) return;

            long key = ((long)senderId << 32) | (uint)sequence;
            if (!s_Partial.TryGetValue(key, out string[] pieces) || pieces.Length != parts)
            {
                pieces = new string[parts];
                s_Partial[key] = pieces;
                if (s_Partial.Count > 64) s_Partial.Clear(); // lost pieces never pile up
                s_Partial[key] = pieces;
            }
            pieces[part] = chunk;
            foreach (string piece in pieces)
            {
                if (piece == null) return;
            }
            s_Partial.Remove(key);

            // Typed chat isn't shown in a Message Builder Chat Only game, or to a player who only wants the builder.
            if (!TypingAllowed) return;

            string text = Clean(string.Concat(pieces));
            if (text.Length == 0) return;
            AddLine(senderId, NameFilter.ForDisplay(text), typed: true);
        }

        internal static void ReceiveBuilder(int senderId, int targetId, bool should, int action, int when)
        {
            AddBuilderLine(senderId, targetId, should, action, when);
        }

        internal static void ReceivePreset(int senderId, int preset)
        {
            if (!ChatPhrases.IsValidPreset(preset)) return;
            AddLine(senderId, ChatPhrases.Preset(preset), typed: false);
        }

        private static void AddBuilderLine(int senderId, int targetId, bool should, int action, int when)
        {
            string sentence = ChatPhrases.Sentence(NetworkPlayers.GetDisplayName((ulong)targetId), should, action, when);
            if (sentence.Length == 0) return;
            AddLine(senderId, sentence, typed: false);
        }

        private static void AddLine(int senderId, string body, bool typed)
        {
            var line = new ChatLine
            {
                Time = DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                SenderId = senderId,
                SenderName = NetworkPlayers.GetDisplayName((ulong)senderId),
                Body = body,
                Typed = typed,
            };
            s_Lines.Add(line);
            if (s_Lines.Count > MaxLines) s_Lines.RemoveRange(0, s_Lines.Count - MaxLines);
            Added?.Invoke(line);
        }

        /// <summary>No control characters (line breaks become spaces), trimmed, at most <see cref="MaxLength"/> characters.</summary>
        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var builder = new StringBuilder(Mathf.Min(text.Length, MaxLength));
            foreach (char c in text)
            {
                if (builder.Length >= MaxLength) break;
                if (c == '\n' || c == '\r' || c == '\t') builder.Append(' ');
                else if (!char.IsControl(c)) builder.Append(c);
            }
            return builder.ToString().Trim();
        }

        /// <summary>The line as rich text: grey time, gold name, then the message - names and messages can't use tags.</summary>
        public static string ToRichText(ChatLine line)
        {
            return $"<color=#9A9A9A>{line.Time}</color> <color=#FFD45A>{NoParse(line.SenderName)}:</color> {NoParse(line.Body)}";
        }

        private static string NoParse(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace("<noparse>", "").Replace("</noparse>", "").Replace("<NOPARSE>", "").Replace("</NOPARSE>", "");
            return "<noparse>" + text + "</noparse>";
        }
    }

    /// <summary>
    /// Holds the chat's static RPCs (static RPCs need a SimulationBehaviour to live on, but no instance or
    /// NetworkObject - same trick as PlayerProfileSync). Never added to anything.
    /// </summary>
    public class ChatSync : SimulationBehaviour
    {
        /// <summary>One piece of a typed message.</summary>
        [Rpc]
        public static void TextRpc(NetworkRunner runner, int sequence, int part, int parts, string chunk, RpcInfo info = default)
        {
            if (!TryGetRemoteSender(runner, info, out int sender)) return;
            ChatLog.ReceiveTextPart(sender, sequence, part, parts, chunk);
        }

        /// <summary>A built sentence: "[target] should / should not [action] [when]".</summary>
        [Rpc]
        public static void BuilderRpc(NetworkRunner runner, int targetId, NetworkBool should, int action, int when, RpcInfo info = default)
        {
            if (!TryGetRemoteSender(runner, info, out int sender)) return;
            ChatLog.ReceiveBuilder(sender, targetId, should, action, when);
        }

        /// <summary>A set phrase.</summary>
        [Rpc]
        public static void PresetRpc(NetworkRunner runner, int preset, RpcInfo info = default)
        {
            if (!TryGetRemoteSender(runner, info, out int sender)) return;
            ChatLog.ReceivePreset(sender, preset);
        }

        /// <summary>Another player's message (the sender already added its own).</summary>
        private static bool TryGetRemoteSender(NetworkRunner runner, RpcInfo info, out int sender)
        {
            sender = 0;
            if (!info.Source.IsRealPlayer || info.Source == runner.LocalPlayer) return false;
            sender = info.Source.PlayerId;
            return true;
        }
    }
}
