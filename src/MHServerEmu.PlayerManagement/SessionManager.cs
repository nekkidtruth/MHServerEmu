﻿using Gazillion;
using MHServerEmu.Core.Config;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.System;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Frontend;
using MHServerEmu.Games;
using MHServerEmu.PlayerManagement.Configs;

namespace MHServerEmu.PlayerManagement
{
    public class SessionManager
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private readonly PlayerManagerService _playerManager;

        private readonly IdGenerator _idGenerator = new(IdType.Session, 0);

        private readonly object _sessionLock = new();
        private readonly Dictionary<ulong, ClientSession> _sessionDict = new();
        private readonly Dictionary<ulong, FrontendClient> _clientDict = new();

        public int SessionCount { get => _sessionDict.Count; }

        public SessionManager(PlayerManagerService playerManager)
        {
            _playerManager = playerManager;
        }

        public AuthStatusCode TryCreateSessionFromLoginDataPB(LoginDataPB loginDataPB, out ClientSession session)
        {
            session = null;

            // Check client version
            if (loginDataPB.Version != Game.Version)
            {
                Logger.Warn($"TryCreateSessionFromLoginDataPB(): Client version mismatch ({loginDataPB.Version} instead of {Game.Version})");

                // Fail auth if version mismatch is not allowed
                if (_playerManager.Config.AllowClientVersionMismatch == false)
                    return AuthStatusCode.PatchRequired;
            }

            // Verify credentials
            DBAccount account;
            AuthStatusCode statusCode;

            if (_playerManager.Config.BypassAuth)  // Auth always succeeds when BypassAuth is set to true
            {
                account = AccountManager.DefaultAccount;
                statusCode = AuthStatusCode.Success;
            }
            else                                    // Check credentials with AccountManager
            {
                statusCode = AccountManager.TryGetAccountByLoginDataPB(loginDataPB, out account);
            }

            // Create a new session if login data is valid
            if (statusCode == AuthStatusCode.Success)
            {
                lock (_sessionLock)
                {
                    session = new(_idGenerator.Generate(), account, loginDataPB.ClientDownloader, loginDataPB.Locale);
                    _sessionDict.Add(session.Id, session);
                }
            }

            return statusCode;
        }

        public bool VerifyClientCredentials(FrontendClient client, ClientCredentials credentials)
        {
            // Check if the session exists
            if (_sessionDict.TryGetValue(credentials.Sessionid, out ClientSession session) == false)
            {
                Logger.Warn($"VerifyClientCredentials(): SessionId {credentials.Sessionid} not found");
                return false;
            }

            // Try to decrypt the token
            if (CryptographyHelper.TryDecryptToken(credentials.EncryptedToken.ToByteArray(), session.Key,
                credentials.Iv.ToByteArray(), out byte[] decryptedToken) == false)
            {
                Logger.Warn($"VerifyClientCredentials(): Failed to decrypt token for sessionId {session.Id}");
                lock (_sessionLock) _sessionDict.Remove(session.Id);    // invalidate session after a failed login attempt
                return false;
            }

            // Verify the token
            if (CryptographyHelper.VerifyToken(decryptedToken, session.Token) == false)
            {
                Logger.Warn($"VerifyClientCredentials(): Failed to verify token for sessionId {session.Id}");
                lock (_sessionLock) _sessionDict.Remove(session.Id);    // invalidate session after a failed login attempt
                return false;
            }

            Logger.Info($"Verified client for sessionId {session.Id} - account {session.Account}");

            // Assign account to the client if the token is valid
            lock (_sessionLock)
            {
                client.AssignSession(session);
                _clientDict.Add(session.Id, client);
                return true;
            }
        }

        public void RemoveSession(ulong sessionId)
        {
            lock (_sessionLock)
            {
                _sessionDict.Remove(sessionId);
                _clientDict.Remove(sessionId);
            }
        }

        public bool TryGetSession(ulong sessionId, out ClientSession session) => _sessionDict.TryGetValue(sessionId, out session);
        public bool TryGetClient(ulong sessionId, out FrontendClient client) => _clientDict.TryGetValue(sessionId, out client);
    }
}