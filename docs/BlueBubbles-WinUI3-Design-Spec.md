# BlueBubbles WinUI3 Client — Design Specification

This document is the authoritative reference for building a WinUI3 iMessage client that communicates with the BlueBubbles macOS server. Claude Code should treat every section as a binding constraint unless explicitly told otherwise by the developer.

---

## 1. Project Identity

- **Name**: BlueBubbles for Windows (working title)
- **Framework**: WinUI 3 (Windows App SDK, latest stable)
- **Language**: C# (.NET 8+)
- **MVVM**: CommunityToolkit.Mvvm
- **Target**: Windows 10 19041+ / Windows 11
- **Design system**: Fluent 2 — match the visual language of Windows 11 Settings, Calculator, and the WinUI 3 Gallery app.

---

## 2. Architecture Overview

```
┌────────────────────────────────────────────────────────┐
│                    WinUI3 Views (XAML)                  │
│  ConversationListPage │ ChatPage │ SettingsPage │ etc  │
├────────────────────────────────────────────────────────┤
│                    ViewModels (C#)                      │
│  CommunityToolkit.Mvvm ObservableObject / RelayCommand │
├────────────────────────────────────────────────────────┤
│                    Services Layer                       │
│  BlueBubblesApiService │ SocketService │ ContactService │
│  NotificationService   │ AttachmentCacheService         │
│  FirebaseService       │ ServerDiscoveryService         │
├────────────────────────────────────────────────────────┤
│                    Models (C# records/classes)          │
│  Chat │ Message │ Handle │ Attachment │ Contact │ etc  │
├────────────────────────────────────────────────────────┤
│                    External                             │
│  BlueBubbles Server (macOS) ←── REST + Socket.IO ──→   │
│  Firebase (RTDB / Firestore) ←── server URL store ──→  │
│  Proxy: Cloudflare Tunnel │ ngrok │ zrok │ LAN         │
└────────────────────────────────────────────────────────┘
```

**Dependency direction**: Views → ViewModels → Services → Models. Views never call services directly. ViewModels never reference XAML types.

**Dependency injection**: Use `Microsoft.Extensions.DependencyInjection`. Register services as singletons (ApiService, SocketService) or transient (per-page ViewModels) in `App.xaml.cs`.

---

## 3. Solution Structure

```
BlueBubbles.Windows/
├── BlueBubbles.Windows/              # WinUI3 app project
│   ├── App.xaml / App.xaml.cs
│   ├── MainWindow.xaml / .cs
│   ├── Views/
│   │   ├── ShellPage.xaml            # Root layout with NavigationView
│   │   ├── ConversationListPage.xaml  # Left pane: search + pinned + chat list
│   │   ├── ChatPage.xaml             # Right pane: message thread + composer
│   │   ├── ChatDetailsPage.xaml      # Conversation info, participants, media
│   │   ├── NewChatPage.xaml          # Chat creator / contact picker
│   │   ├── Settings/
│   │   │   ├── SettingsPage.xaml     # Settings root (NavigationView Left)
│   │   │   ├── ConnectionPage.xaml   # Server URL, password, connection status
│   │   │   ├── NotificationsPage.xaml
│   │   │   ├── AppearancePage.xaml   # Theme, density, avatars
│   │   │   ├── MessagingPage.xaml    # Send behavior, timestamps, read receipts
│   │   │   ├── PrivateApiPage.xaml   # Private API toggles
│   │   │   ├── BackupPage.xaml       # Theme/settings backup/restore
│   │   │   ├── ServerPage.xaml       # Server info, stats, restart, update
│   │   │   └── AboutPage.xaml
│   │   ├── Setup/
│   │   │   ├── WelcomePage.xaml
│   │   │   ├── ServerConnectPage.xaml     # Manual URL + password entry
│   │   │   ├── GoogleSignInPage.xaml      # WebView2 OAuth + project picker
│   │   │   ├── SyncPage.xaml
│   │   │   └── SetupCompletePage.xaml
│   │   └── FindMy/
│   │       └── FindMyPage.xaml
│   ├── ViewModels/
│   │   ├── ShellViewModel.cs
│   │   ├── ConversationListViewModel.cs
│   │   ├── ChatViewModel.cs
│   │   ├── ChatDetailsViewModel.cs
│   │   ├── NewChatViewModel.cs
│   │   ├── Settings/
│   │   │   └── (one per settings page)
│   │   └── Setup/
│   │       └── SetupViewModel.cs
│   ├── Controls/                     # Reusable custom controls
│   │   ├── ChatBubble.xaml           # Single message bubble
│   │   ├── ConversationTile.xaml     # Chat list item
│   │   ├── PinnedContact.xaml        # Circular avatar + name
│   │   ├── MessageComposer.xaml      # Text input + attachment + send
│   │   ├── TypingIndicator.xaml
│   │   ├── ConnectionStatusBadge.xaml
│   │   └── AvatarControl.xaml        # Contact photo or initials fallback
│   ├── Converters/
│   ├── Helpers/
│   └── Assets/
├── BlueBubbles.Core/                 # Class library (no UI references)
│   ├── Models/
│   │   ├── Chat.cs
│   │   ├── Message.cs
│   │   ├── Handle.cs
│   │   ├── Attachment.cs
│   │   ├── Contact.cs
│   │   ├── StructuredName.cs
│   │   ├── AttributedBody.cs
│   │   ├── MessagePart.cs
│   │   ├── ServerInfo.cs
│   │   ├── ScheduledMessage.cs
│   │   ├── FindMyDevice.cs
│   │   ├── FindMyFriend.cs
│   │   ├── FcmData.cs
│   │   ├── FirebaseProject.cs          # Project list from Google API
│   │   ├── ApiResponse.cs            # Generic { status, message, data }
│   │   └── SocketEvents.cs           # Event type constants + payload types
│   ├── Services/
│   │   ├── BlueBubblesApiService.cs   # 1:1 port of http_service.dart
│   │   ├── SocketService.cs           # Socket.IO real-time events
│   │   ├── AttachmentCacheService.cs  # Download + local file cache
│   │   ├── ContactResolverService.cs  # Address → display name mapping
│   │   ├── FirebaseService.cs         # FCM config, URL fetch from RTDB/Firestore
│   │   ├── ServerDiscoveryService.cs  # Google OAuth → project list → URL resolution
│   │   └── LocalhostDetectionService.cs # LAN server detection + port scanning
│   └── Configuration/
│       └── ServerConfiguration.cs     # URL, password, proxy type, custom headers, FCM data
└── BlueBubbles.Windows.Tests/
```

---

## 4. API Service Layer

This is a direct C# translation of the official Flutter client's `lib/services/network/http_service.dart`. Use `System.Net.Http.HttpClient` (singleton, injected). All methods return `Task<ApiResponse<T>>` where `ApiResponse<T>` wraps the server's `{ status, message, data }` envelope.

### 4.1 Authentication

Every request appends `?guid={password}` as a query parameter. Store the password in `Windows.Security.Credentials.PasswordVault`, never in plaintext settings.

```csharp
private string BuildUrl(string path, Dictionary<string, string>? extraParams = null)
{
    var uri = new UriBuilder($"{_baseUrl}/api/v1/{path}");
    var query = HttpUtility.ParseQueryString(uri.Query);
    query["guid"] = _password;
    if (extraParams != null)
        foreach (var kv in extraParams)
            query[kv.Key] = kv.Value;
    uri.Query = query.ToString();
    return uri.ToString();
}
```

### 4.2 Custom Headers

The Flutter client adds `ngrok-skip-browser-warning: true` for ngrok URLs and `skip_zrok_interstitial: true` for zrok URLs. Replicate this logic in a `DelegatingHandler`.

### 4.3 Endpoint Reference

Port every method from `http_service.dart`. Below is the complete list with HTTP verb, path, parameters, and request body. Claude Code must implement all of these.

#### Server

| Method | Dart name | HTTP | Path | Body/Params |
|--------|-----------|------|------|-------------|
| Ping | `ping()` | GET | `/ping` | — |
| Server Info | `serverInfo()` | GET | `/server/info` | Cache 1 min |
| Soft Restart | `softRestart()` | GET | `/server/restart/soft` | — |
| Hard Restart | `hardRestart()` | GET | `/server/restart/hard` | — |
| Check Update | `checkUpdate()` | GET | `/server/update/check` | — |
| Install Update | `installUpdate()` | POST | `/server/update/install` | — |
| Stat Totals | `serverStatTotals()` | GET | `/server/statistics/totals` | — |
| Stat Media | `serverStatMedia()` | GET | `/server/statistics/media` | `?byChat` opt |
| Logs | `serverLogs()` | GET | `/server/logs` | `?count=10000` |
| Lock Mac | `lockMac()` | POST | `/mac/lock` | — |
| Restart iMessage | `restartImessage()` | POST | `/mac/imessage/restart` | — |

#### FCM

| Method | HTTP | Path | Body |
|--------|------|------|------|
| Add Device | POST | `/fcm/device` | `{ name, identifier }` |
| Get Client | GET | `/fcm/client` | — |

#### Attachments

| Method | HTTP | Path | Notes |
|--------|------|------|-------|
| Get Info | GET | `/attachment/{guid}` | — |
| Download | GET | `/attachment/{guid}/download` | ResponseType: bytes. `?original=bool` |
| Live Photo | GET | `/attachment/{guid}/live` | ResponseType: bytes |
| Blurhash | GET | `/attachment/{guid}/blurhash` | ResponseType: bytes |
| Count | GET | `/attachment/count` | — |

#### Chats

| Method | HTTP | Path | Body/Params |
|--------|------|------|-------------|
| Query | POST | `/chat/query` | `{ with: [], offset, limit, sort }`. `with` options: `"participants"`, `"lastmessage"`, `"sms"`, `"archived"` |
| Single | GET | `/chat/{guid}` | `?with=` (comma-separated) |
| Messages | GET | `/chat/{guid}/message` | `?with=&sort=DESC&before=&after=&offset=0&limit=100` |
| Count | GET | `/chat/count` | — |
| Create | POST | `/chat/new` | `{ addresses: [], message, service, method }` |
| Update | PUT | `/chat/{guid}` | `{ displayName }` |
| Delete | DELETE | `/chat/{guid}` | — |
| Mark Read | POST | `/chat/{guid}/read` | — |
| Mark Unread | POST | `/chat/{guid}/unread` | — |
| Get Icon | GET | `/chat/{guid}/icon` | ResponseType: bytes |
| Set Icon | POST | `/chat/{guid}/icon` | Multipart: `icon` file |
| Delete Icon | DELETE | `/chat/{guid}/icon` | — |
| Add/Remove Participant | POST | `/chat/{guid}/participant/{add\|remove}` | `{ address }` |
| Leave | POST | `/chat/{guid}/leave` | — |
| Delete Message | DELETE | `/chat/{guid}/{messageGuid}` | — |

#### Messages

| Method | HTTP | Path | Body/Params |
|--------|------|------|-------------|
| Query | POST | `/message/query` | `{ with: [], where: [], sort, before, after, chatGuid, offset, limit, convertAttachments }`. `with` options: `"chats"`, `"attachment"`, `"handle"`, `"chats.participants"`, `"attachment.metadata"`, `"attributedBody"` |
| Single | GET | `/message/{guid}` | `?with=` |
| Embedded Media | GET | `/message/{guid}/embedded-media` | ResponseType: bytes |
| Count | GET | `/message/count` | `?after=&before=` (ms timestamps) |
| Count Updated | GET | `/message/count/updated` | `?after=&before=` |
| Count Me | GET | `/message/count/me` | `?after=&before=` |
| Send Text | POST | `/message/text` | `{ chatGuid, tempGuid, message, method, effectId?, subject?, selectedMessageGuid?, partIndex?, ddScan? }` |
| Send Attachment | POST | `/message/attachment` | Multipart: `attachment`, `chatGuid`, `tempGuid`, `name`, `method`. Optional: `effectId`, `subject`, `selectedMessageGuid`, `partIndex`, `isAudioMessage` |
| Send Multipart | POST | `/message/multipart` | `{ chatGuid, tempGuid, parts: [], effectId?, subject?, selectedMessageGuid?, partIndex?, ddScan? }` |
| React (Tapback) | POST | `/message/react` | `{ chatGuid, selectedMessageText, selectedMessageGuid, reaction, partIndex? }` |
| Unsend | POST | `/message/{guid}/unsend` | `{ partIndex: 0 }` |
| Edit | POST | `/message/{guid}/edit` | `{ editedMessage, backwardsCompatibilityMessage, partIndex: 0 }` |
| Notify | POST | `/message/{guid}/notify` | — |
| Get Scheduled | GET | `/message/schedule` | — |
| Create Scheduled | POST | `/message/schedule` | `{ type: "send-message", payload: { chatGuid, message, method }, scheduledFor: msTimestamp, schedule: {} }` |
| Update Scheduled | PUT | `/message/schedule/{id}` | same as create |
| Delete Scheduled | DELETE | `/message/schedule/{id}` | — |

#### Handles

| Method | HTTP | Path | Body/Params |
|--------|------|------|-------------|
| Query | POST | `/handle/query` | `{ with: [], address?, offset, limit }` |
| Single | GET | `/handle/{guid}` | — |
| Focus State | GET | `/handle/{address}/focus` | — |
| iMessage Availability | GET | `/handle/availability/imessage` | `?address=` |
| FaceTime Availability | GET | `/handle/availability/facetime` | `?address=` |
| Count | GET | `/handle/count` | — |

#### Contacts

| Method | HTTP | Path | Body/Params |
|--------|------|------|-------------|
| Get All | GET | `/contact` | `?extraProperties=avatar` opt |
| Query by Address | POST | `/contact/query` | `{ addresses: [] }` |
| Create | POST | `/contact` | Array of contact objects |

#### iCloud / FindMy

| Method | HTTP | Path |
|--------|------|------|
| FindMy Devices | GET | `/icloud/findmy/devices` |
| Refresh Devices | POST | `/icloud/findmy/devices/refresh` |
| FindMy Friends | GET | `/icloud/findmy/friends` |
| Refresh Friends | POST | `/icloud/findmy/friends/refresh` |
| Account Info | GET | `/icloud/account` |
| Account Contact | GET | `/icloud/contact` |
| Set Alias | POST | `/icloud/account/alias` — `{ alias }` |

#### FaceTime

| Method | HTTP | Path |
|--------|------|------|
| Answer | POST | `/facetime/answer/{callUuid}` |
| Leave | POST | `/facetime/leave/{callUuid}` |

#### Backup

| Method | HTTP | Path | Body |
|--------|------|------|------|
| Get Theme | GET | `/backup/theme` | — |
| Set Theme | POST | `/backup/theme` | `{ name, data: {} }` |
| Delete Theme | DELETE | `/backup/theme` | `{ name }` |
| Get Settings | GET | `/backup/settings` | — |
| Set Settings | POST | `/backup/settings` | `{ name, data: {} }` |
| Delete Settings | DELETE | `/backup/settings` | `{ name }` |

### 4.4 Response Envelope

All responses conform to:

```json
{
  "status": 200,
  "message": "Success",
  "data": { ... }
}
```

Error responses:

```json
{
  "status": 400,
  "message": "Bad Request",
  "error": {
    "type": "Error",
    "error": "Description"
  }
}
```

Model this as:

```csharp
public record ApiResponse<T>(int Status, string Message, T? Data, ApiError? Error);
public record ApiError(string Type, string ErrorMessage);
```

### 4.5 Timeout Strategy

Default connect timeout: 15s. Default send/receive timeout: configurable (default 30s, stored in settings). Attachment downloads use 12× the base timeout. Retry once on 502 if the server URL contains `trycloudflare`.

---

## 5. Socket.IO Real-Time Layer

Use `SocketIOClient` NuGet package. Connect with query `{ guid: password }` and transports `["websocket", "polling"]`.

### 5.1 Events to Listen On

These events drive the live UI. All event handlers should dispatch to the UI thread via `DispatcherQueue.TryEnqueue`.

| Event | Payload | UI Effect |
|-------|---------|-----------|
| `new-message` | Message JSON | Insert into chat, update conversation list order, show notification if not focused |
| `updated-message` | Message JSON | Update delivery/read status, edit content, unsend |
| `typing-indicator` | `{ display: bool, guid: chatGuid }` | Show/hide typing indicator in chat |
| `chat-read-status-changed` | Chat JSON | Update read/unread badge |
| `group-name-change` | Chat JSON | Update conversation tile display name |
| `participant-added` | Chat JSON | Update participant list |
| `participant-removed` | Chat JSON | Update participant list |
| `participant-left` | Chat JSON | Update participant list |
| `incoming-facetime` | Call data (JSON string, must decode) | Show incoming call notification |
| `ft-call-status-changed` | Call data | Update call status |
| `imessage-aliases-removed` | Data | Handle alias removal |

### 5.2 Connection State Machine

```
DISCONNECTED → CONNECTING → CONNECTED
     ↑              ↑            │
     │              │            │ (error/disconnect)
     │              └────────────┘
     │                    │
     └────── ERROR ◄──────┘
              │
              └─ (5s timer) → fetch new URL from Firebase (§6.4) → restart socket
```

Display connection state via `InfoBar` at the top of the conversation list:
- Connected: no bar shown (clean state)
- Connecting: `InfoBar Severity="Informational"` "Connecting to server..."
- Error: `InfoBar Severity="Error"` "Connection lost. Retrying..."
- Disconnected: `InfoBar Severity="Warning"` "Disconnected"

### 5.3 Encrypted Responses

The socket may return `{ encrypted: true, data: encryptedString }`. Decrypt using AES (CryptoJS-compatible) with the server password as the key. Port the `decryptAESCryptoJS` function from the Flutter client.

---

## 6. Server Discovery & Proxy Services

The BlueBubbles server's public URL is dynamic when using Cloudflare Tunnels, ngrok, or zrok. The server writes its current URL to Firebase (Realtime Database or Cloud Firestore). The client discovers and resolves this URL via Google OAuth + Firebase, then connects. This is the primary connection method — manual URL entry is the fallback.

### 6.1 Google OAuth Flow

Use `WebView2` (via `Microsoft.Web.WebView2`) to present the Google sign-in page. This matches the Flutter desktop client's approach using `desktop_webview_auth`.

**OAuth Configuration:**
- Client ID for desktop: `500464701389-18rfq995s6dqo3e5d3n2e7i3ljr0uc9i.apps.googleusercontent.com` (from the official BlueBubbles project)
- Redirect URI: `http://localhost:8641/oauth/callback`
- Required scopes:
  - `https://www.googleapis.com/auth/cloudplatformprojects`
  - `https://www.googleapis.com/auth/firebase`
  - `https://www.googleapis.com/auth/datastore`

**Flow:**
1. Open a `WebView2` window pointed at Google's OAuth consent screen with the scopes above.
2. Google redirects to `http://localhost:8641/oauth/callback` with an authorization code.
3. Exchange the code for an access token.
4. Fetch user info: `GET https://www.googleapis.com/oauth2/v1/userinfo?access_token={token}` — returns `{ name, picture }` for display.
5. Proceed to Firebase project discovery (6.2).

### 6.2 Firebase Project Discovery

After obtaining the Google access token:

1. **List Firebase projects**: `GET https://firebase.googleapis.com/v1beta1/projects?access_token={token}` — returns `{ results: [ { projectId, displayName, resources: { realtimeDatabaseInstance? } } ] }`.

2. **Resolve server URL from each project** — two storage backends, try in order:
   - **Realtime Database** (if `resources.realtimeDatabaseInstance` is present): `GET https://{realtimeDatabaseInstance}.firebaseio.com/config.json?token={accessToken}` — returns `{ serverUrl: "https://xxx.trycloudflare.com" }`.
   - **Cloud Firestore** (fallback): `GET https://firestore.googleapis.com/v1/projects/{projectId}/databases/(default)/documents/server/config?access_token={token}` — returns `{ fields: { serverUrl: { stringValue: "..." } } }`.

3. **Reachability check**: For each project that yields a `serverUrl`, ping it: `GET {serverUrl}/api/v1/ping`. Show status (Reachable / Unreachable / Checking) next to each project in the UI.

4. **User selects project** → prompt for server password → test auth with `GET {serverUrl}/api/v1/ping?guid={password}` → if 200, save and connect. If 401, show "Incorrect password."

### 6.3 FCM Data Model

The server stores its Firebase configuration, which the client fetches via `GET /api/v1/fcm/client`. This returns the `google-services.json` structure:

```csharp
public class FcmData
{
    public string? ProjectId { get; set; }         // project_info.project_id
    public string? StorageBucket { get; set; }     // project_info.storage_bucket
    public string? ApiKey { get; set; }            // client[0].api_key[0].current_key
    public string? FirebaseUrl { get; set; }       // project_info.firebase_url (RTDB URL, nullable)
    public string? ClientId { get; set; }          // client[0].oauth_client[0].client_id (truncated at first '-')
    public string? ApplicationId { get; set; }     // client[0].client_info.mobilesdk_app_id

    public bool IsValid => ProjectId != null && ApiKey != null && ApplicationId != null;

    public static FcmData FromServerResponse(Dictionary<string, object> data)
    {
        var projectInfo = (Dictionary<string, object>)data["project_info"];
        var client = ((List<object>)data["client"])[0] as Dictionary<string, object>;
        var oauthClient = ((List<object>)client["oauth_client"])[0] as Dictionary<string, object>;
        var clientId = oauthClient["client_id"].ToString()!;

        return new FcmData
        {
            ProjectId = projectInfo["project_id"]?.ToString(),
            StorageBucket = projectInfo["storage_bucket"]?.ToString(),
            ApiKey = ((List<object>)client["api_key"])[0] is Dictionary<string, object> ak
                ? ak["current_key"]?.ToString() : null,
            FirebaseUrl = projectInfo.ContainsKey("firebase_url")
                ? projectInfo["firebase_url"]?.ToString() : null,
            ClientId = clientId.Contains('-') ? clientId[..clientId.IndexOf('-')] : clientId,
            ApplicationId = ((Dictionary<string, object>)client["client_info"])["mobilesdk_app_id"]?.ToString()
        };
    }
}
```

Persist `FcmData` locally (SQLite or `ApplicationData`) so the client can re-fetch the server URL on reconnection without requiring the user to re-authenticate with Google.

### 6.4 Dynamic URL Re-resolution

When the socket connection enters the ERROR state (see §5.2), the client must fetch a fresh server URL from Firebase before reconnecting. This handles Cloudflare Tunnel URL rotation:

1. Load persisted `FcmData`.
2. If `FcmData.FirebaseUrl` is non-null (RTDB): use `firebase_dart`-equivalent logic — initialize a Firebase app with the stored `ApiKey`, `ApplicationId`, `ProjectId`, and `FirebaseUrl`, then read `config/serverUrl` from RTDB.
3. If `FcmData.FirebaseUrl` is null (Firestore): `GET https://firestore.googleapis.com/v1/projects/{projectId}/databases/(default)/documents/server/config` using stored credentials.
4. Sanitize and save the new URL, then restart the socket.

**For desktop (WinUI3)**: Use the `Firebase.Database` REST API directly via `HttpClient` rather than a Firebase SDK. The Flutter client uses `firebase_dart` on desktop — the C# equivalent is direct HTTP calls to the Firebase REST endpoints listed above. No Firebase C# SDK is needed.

### 6.5 Proxy Service Handling

The server reports its proxy type via `GET /api/v1/server/info` → `data.proxy_service` (string: `"cloudflare"`, `"ngrok"`, `"zrok"`, `"lan"`, `"dynamic-dns"`, etc.).

**URL sanitization rules** (from `sanitizeServerAddress`):
- Strip quotes and whitespace.
- If no scheme is present and the URL contains `ngrok.io`, `trycloudflare.com`, or `zrok.io`, prefix with `https://`.
- Otherwise, prefix with `http://`.

**Custom headers per proxy** (from `http_service.dart` headers getter):
- ngrok URLs: add `ngrok-skip-browser-warning: true`
- zrok URLs: add `skip_zrok_interstitial: true`

Implement this in a `DelegatingHandler`:

```csharp
public class ProxyHeaderHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var host = request.RequestUri?.Host ?? "";
        if (host.Contains("ngrok"))
            request.Headers.TryAddWithoutValidation("ngrok-skip-browser-warning", "true");
        else if (host.Contains("zrok"))
            request.Headers.TryAddWithoutValidation("skip_zrok_interstitial", "true");
        return base.SendAsync(request, ct);
    }
}
```

**Cloudflare 502 retry**: If a request returns HTTP 502 and the server URL contains `trycloudflare`, retry the request once before failing. Cloudflare tunnels occasionally return transient 502s during tunnel rotation.

### 6.6 Localhost Detection

When the user configures a `localhostPort` in settings, the client attempts to reach the server directly on the LAN instead of through the proxy tunnel. This reduces latency when the Windows client and Mac server are on the same network.

**Detection flow** (runs on every socket connect, only if `localhostPort` is set):

1. Fetch server info: `GET /api/v1/server/info` → `data.local_ipv4s` (array of strings) and `data.local_ipv6s` (array of strings).
2. For each IP (IPv6 first if `useLocalIpv6` is enabled, then IPv4):
   - Try `https://{ip}:{localhostPort}/api/v1/ping` and `http://{ip}:{localhostPort}/api/v1/ping`.
   - If the response contains `"pong"`, use this address as the API origin override.
3. If no server-reported IPs succeed, fall back to a local network port scan on the configured port.
4. If a local address is found, set `originOverride` on the HTTP service. All subsequent API calls use this address instead of the proxy URL.
5. If the device disconnects from WiFi/Ethernet, clear `originOverride` and revert to the proxy URL.

**Settings required**: `localhostPort` (nullable string) and `useLocalIpv6` (bool, default false).

### 6.7 Server Restart via Firebase

The client can trigger a remote server restart via Firebase when the socket connection is lost and the server is unreachable:

```
PATCH https://firestore.googleapis.com/v1/projects/{projectId}/databases/(default)/documents/server/commands?updateMask.fieldPaths=nextRestart
Body: { "fields": { "nextRestart": { "integerValue": <unix_ms_now> } } }
```

This endpoint already exists in `http_service.dart` as `setRestartDateCF`. The server watches this Firestore document and restarts itself when the value changes.

---

## 7. Data Models

All models use `System.Text.Json` serialization with `JsonPropertyName` attributes matching the server's JSON field names. Use C# records for immutable API responses, classes for mutable local state.

### 7.1 Core Models (from server JSON)

```csharp
public record Chat(
    [property: JsonPropertyName("guid")] string Guid,
    [property: JsonPropertyName("chatIdentifier")] string ChatIdentifier,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("participants")] List<Handle>? Participants,
    [property: JsonPropertyName("lastMessage")] Message? LastMessage,
    [property: JsonPropertyName("isArchived")] bool IsArchived,
    [property: JsonPropertyName("isPinned")] bool IsPinned,
    [property: JsonPropertyName("hasUnreadMessage")] bool HasUnreadMessage,
    [property: JsonPropertyName("service")] string? Service
);

public record Message(
    [property: JsonPropertyName("guid")] string Guid,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("subject")] string? Subject,
    [property: JsonPropertyName("isFromMe")] bool IsFromMe,
    [property: JsonPropertyName("dateCreated")] long DateCreated,
    [property: JsonPropertyName("dateDelivered")] long? DateDelivered,
    [property: JsonPropertyName("dateRead")] long? DateRead,
    [property: JsonPropertyName("handle")] Handle? Handle,
    [property: JsonPropertyName("attachments")] List<Attachment>? Attachments,
    [property: JsonPropertyName("associatedMessageGuid")] string? AssociatedMessageGuid,
    [property: JsonPropertyName("associatedMessageType")] int? AssociatedMessageType,
    [property: JsonPropertyName("expressiveSendStyleId")] string? ExpressiveSendStyleId,
    [property: JsonPropertyName("isAudioMessage")] bool IsAudioMessage,
    [property: JsonPropertyName("hasPayloadData")] bool HasPayloadData,
    [property: JsonPropertyName("chats")] List<Chat>? Chats,
    [property: JsonPropertyName("attributedBody")] List<AttributedBody>? AttributedBody
);

public record Handle(
    [property: JsonPropertyName("originalROWID")] int OriginalRowId,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("service")] string Service,
    [property: JsonPropertyName("country")] string? Country
);

public record Attachment(
    [property: JsonPropertyName("guid")] string Guid,
    [property: JsonPropertyName("uti")] string? Uti,
    [property: JsonPropertyName("mimeType")] string? MimeType,
    [property: JsonPropertyName("transferName")] string? TransferName,
    [property: JsonPropertyName("totalBytes")] long TotalBytes,
    [property: JsonPropertyName("height")] int? Height,
    [property: JsonPropertyName("width")] int? Width
);

public record Contact(
    [property: JsonPropertyName("firstName")] string? FirstName,
    [property: JsonPropertyName("lastName")] string? LastName,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("phones")] List<ContactAddress>? Phones,
    [property: JsonPropertyName("emails")] List<ContactAddress>? Emails,
    [property: JsonPropertyName("avatar")] string? Avatar  // base64
);

public record ContactAddress(
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("label")] string? Label
);
```

### 7.2 Local Persistence

Use SQLite via `Microsoft.Data.Sqlite` or `Entity Framework Core SQLite` for local message/chat caching. This enables offline browsing and faster startup. The Flutter client uses ObjectBox; the WinUI3 client should use EF Core with a similar schema.

---

## 8. UI Design — Fluent 2

### 8.1 Global Principles

- **Mica backdrop** on `MainWindow`. Use `SystemBackdrop = new MicaBackdrop()`.
- **Custom TitleBar** using `Window.ExtendsContentIntoTitleBar = true` and `Window.SetTitleBar()`. Place the app title and connection status in the title bar area.
- **Rounded corners**: all cards, buttons, inputs use the default WinUI3 `CornerRadius` (4px for controls, 8px for cards/surfaces).
- **Color**: use system theme resources (`ApplicationThemeResources`). No hardcoded colors. The accent color drives selection and interactive element highlighting.
- **Typography**: use the Fluent type ramp — `TitleTextBlockStyle`, `SubtitleTextBlockStyle`, `BodyTextBlockStyle`, `CaptionTextBlockStyle`.
- **Spacing**: 4px grid. Margins and padding in multiples of 4. Match Windows 11 Settings density.
- **Light and Dark theme**: both must work. Rely on theme resources, never hardcode colors.
- **Animations**: use WinUI3 implicit animations and `ConnectedAnimation` for page transitions. Keep motion subtle and functional, not decorative.

### 8.2 Shell Layout

The app uses a **two-column master-detail layout**, not a NavigationView hamburger menu. This is a messaging app, not a settings app.

```
┌──────────────────────────────────────────────────────────┐
│  [TitleBar: App Name          Connection ● ── ═ ✕]       │
├────────────────────┬─────────────────────────────────────┤
│  ┌──────────────┐  │                                     │
│  │  🔍 Search    │  │   Chat Header (name, avatar, ⓘ)    │
│  └──────────────┘  │─────────────────────────────────────│
│                    │                                     │
│  ┌─Pinned────────┐ │   Message Thread                    │
│  │ ○ ○ ○         │ │   (ScrollViewer, bottom-anchored)   │
│  │ ○ ○ ○         │ │                                     │
│  └───────────────┘ │         ┌──────────────┐            │
│                    │         │  Their msg   │            │
│  ┌─Conversations─┐ │         └──────────────┘            │
│  │ ● Name  12:30 │ │   ┌──────────────┐                  │
│  │   Preview...  │ │   │   Your msg   │                  │
│  │───────────────│ │   └──────────────┘                  │
│  │   Name  Yday  │ │                                     │
│  │   Preview...  │ │         ┌──────────────┐            │
│  │───────────────│ │         │  Their msg   │            │
│  │   Name  Sat   │ │         └──────────────┘            │
│  │   Preview...  │ │                                     │
│  └───────────────┘ │─────────────────────────────────────│
│                    │  [+ 📎] [  Message input...  ] [➤]  │
├────────────────────┴─────────────────────────────────────┤
│  [⚙ Settings]   [optional status bar]                    │
└──────────────────────────────────────────────────────────┘
```

**Left pane** (320px default, resizable 280-400px):
- Search box (`AutoSuggestBox`) at top
- Pinned conversations section: horizontal wrap of `PinnedContact` controls (circular `PersonPicture` + caption). Matches the macOS Messages pinned section.
- Conversation list: `ListView` with `ConversationTile` items. Each tile shows avatar, display name, timestamp, message preview (2 lines max, truncated). Unread indicator: bold name + accent-colored dot. Selected tile: system highlight.
- Settings button at the bottom of the pane (gear icon), or accessible via `NavigationViewItem` at the footer.

**Right pane** (fills remaining space):
- Chat header bar: display name, participant count for groups, `PersonPicture`(s), info button (ⓘ) to open `ChatDetailsPage`.
- Message thread: `ItemsRepeater` or `ListView` in a `ScrollViewer`. Bottom-anchored (newest messages at bottom, load older on scroll-up).
- Message composer bar: `TextBox` (multi-line, auto-grow), attachment button (opens file picker), send button. Optional: emoji picker button, audio message button.

**Empty state** (no chat selected): centered illustration or text — "Select a conversation to start messaging."

### 8.3 Adaptive / Responsive Behavior

- **≥ 768px width**: show both panes (master-detail). This is the primary mode.
- **< 768px width**: single-pane navigation. Show conversation list; selecting a chat navigates to the chat page (full width); back button returns to list. Use `VisualStateManager` triggers.

### 8.4 Conversation Tile (`ConversationTile` Control)

```
┌────────────────────────────────────────┐
│  ┌────┐  Contact Name          2:30PM │
│  │ PP │  Last message preview text    │
│  │    │  that wraps to second line... │
│  └────┘                          ●    │
└────────────────────────────────────────┘
```

- `PersonPicture` (40x40) on the left. For groups, use stacked/overlapping `PersonPicture` or the group icon from the server.
- Name: `BodyStrongTextBlockStyle`, bold if unread.
- Timestamp: `CaptionTextBlockStyle`, right-aligned, `TextSecondary` color.
- Preview: `CaptionTextBlockStyle`, 2-line max, `TextSecondary`. If attachment-only, show "Attachment" or "Photo" in italics.
- Unread dot: 8px circle, `AccentFillColorDefaultBrush`, right side.
- Right-click context menu (`MenuFlyout`): Pin/Unpin, Mark Read/Unread, Mute, Archive, Delete.
- Swipe actions (optional): SwipeControl for mark-read (left) and delete (right).

### 8.5 Pinned Contacts (`PinnedContact` Control)

```
    ┌────┐
    │ PP │    ← PersonPicture 56x56
    │    │
    └────┘
   Name         ← CaptionTextBlockStyle, centered, truncated
```

- Grid layout: 3 columns, rows expand as needed. Matches the macOS Messages pinned grid.
- Unread badge: `InfoBadge` (number or dot) overlaid top-right of avatar.
- Tap: select the conversation (same as clicking the tile in the list).
- Long press / right-click: context menu (Unpin, Mark Read).

### 8.6 Chat Bubbles (`ChatBubble` Control)

**Outgoing (from me)**: right-aligned, system accent color background, white text.
**Incoming**: left-aligned, `CardBackgroundFillColorDefaultBrush` background, standard text color.

```
Incoming:                          Outgoing:
┌──────────────────┐                    ┌──────────────────┐
│  Message text    │                    │  Message text    │
│  here            │                    │  here            │
│          2:30 PM │                    │          2:31 PM │
└──────────────────┘                    └──────────────────┘
```

- Rounded corners: 12px for bubble, 4px on the tail corner.
- Max width: 65% of the chat pane width.
- Group chats: show sender name above incoming bubbles in `CaptionTextBlockStyle`.
- Attachments: inline image preview (thumbnail), click to expand. Non-image files: file icon + name + size, click to download/open.
- Reactions (tapbacks): small pill below the bubble showing the reaction icon and count.
- Reply indicator: thin accent-colored left border + quoted text snippet above the message.
- Delivery status (outgoing): ✓ sent, ✓✓ delivered, eye icon read. `CaptionTextBlockStyle`, below timestamp.
- Link previews: `Border` card below message with URL metadata (title, description, image).

### 8.7 Message Composer (`MessageComposer` Control)

```
┌─────────────────────────────────────────────────────────┐
│  [+]  │  Type a message...                     │ [⏵] [➤] │
└─────────────────────────────────────────────────────────┘
```

- `TextBox` with `AcceptsReturn="True"`, auto-grows up to 5 lines, then scrolls.
- `+` button: `DropDownButton` or `MenuFlyout` → File picker, Photo, Contact.
- Send button: enabled only when text is non-empty or attachment is staged.
- Audio message button (`⏵`): hold to record, release to send (if Private API enabled).
- Attachment staging: preview bar above the text box showing thumbnails of queued files, with X to remove.
- Send with Enter (configurable in settings). Shift+Enter for newline when enabled.
- Typing indicator: when user is typing, emit typing event via socket (if Private API + typing indicators enabled).

### 8.8 Settings

Settings uses a **left NavigationView** (matching Windows 11 Settings app). It opens as a new page/navigation context, not a flyout.

Categories and items:

**Connection**
- Server URL (TextBox, read-only after setup, with Edit button)
- Password (PasswordBox, masked)
- Connection status indicator
- Proxy service display (read-only: Cloudflare / ngrok / zrok / LAN / Dynamic DNS)
- Test Connection button
- "Sign in with Google" button — re-run the OAuth + Firebase project discovery flow to update the server URL. Useful when the Cloudflare tunnel URL has rotated.
- Custom headers (key-value editor, Expander)
- Localhost port (nullable TextBox) — when set, enables LAN detection (§6.6)
- Use IPv6 for localhost (ToggleSwitch, shown only when localhost port is set)

**Notifications**
- Enable/disable notifications (ToggleSwitch)
- Notification sound picker
- Notify on chat list (ToggleSwitch)
- Notify for reactions (ToggleSwitch)
- Filter unknown senders (ToggleSwitch)

**Appearance**
- Theme: System / Light / Dark (RadioButtons or ComboBox)
- Colorful avatars (ToggleSwitch)
- 24-hour time format (ToggleSwitch)

**Messaging**
- Auto-download attachments (ToggleSwitch)
- Send with Enter key (ToggleSwitch)
- Show delivery timestamps (ToggleSwitch)
- Show send/receive indicators on chat list (ToggleSwitch)
- Send delay seconds (NumberBox, 0-10)
- Scroll to last unread (ToggleSwitch)
- Your display name (TextBox)

**Private API** (gated: show only if server reports Private API enabled)
- Enable Private API features (ToggleSwitch)
- Send typing indicators (ToggleSwitch)
- Mark chat as read on server (ToggleSwitch)
- Private API send method (ToggleSwitch)
- Private API attachment send (ToggleSwitch)

**Server Management**
- Server info display (OS version, server version, proxy service)
- Server statistics (totals, media counts)
- Soft restart button
- Hard restart button
- Check for updates / install update buttons
- Server logs viewer (read-only TextBox or RichEditBox, monospaced)

**Backup & Restore**
- Theme backup: save / restore / delete
- Settings backup: save / restore / delete

**About**
- App version
- Server version
- Links: GitHub, Discord, documentation

Each settings item should use the standard Fluent 2 pattern: label on the left, control on the right, optional description text below the label in `CaptionTextBlockStyle`. Use `SettingsCard` from `CommunityToolkit.WinUI.Controls` if available, or replicate the pattern with `Grid` rows.

### 8.9 Setup Flow

First-run experience. Full-screen, step-by-step wizard using a `Frame` with forward/back navigation. No NavigationView.

1. **Welcome**: app logo, brief description, "Get Started" button.
2. **Connect to Server** — two paths, tabbed or stacked:
   - **Sign in with Google** (primary, recommended): "Sign in with Google" button styled per Google branding guidelines. Opens `WebView2` for OAuth consent. After auth, shows a list of discovered Firebase projects with server URLs. Each project shows: display name, project ID, server URL, and reachability status (green "Reachable" / red "Unreachable" / yellow "Checking..." with `ProgressRing`). User taps a reachable project → `ContentDialog` prompts for server password → test auth → proceed. "Retry Connections" button if all are unreachable. "Choose a different account" link below the project list.
   - **Manual connection** (fallback): server URL `TextBox`, password `PasswordBox`, "Connect" button. Shows `ProgressRing` during connection test. `InfoBar` for success/error. URL is auto-sanitized (HTTPS for ngrok/cloudflare/zrok, HTTP otherwise).
3. **Initial Sync**: `ProgressBar` or `ProgressRing` showing chat/message sync progress. "This may take a moment" text. Fetch FCM config from server (`GET /fcm/client`) and persist locally for future URL re-resolution.
4. **Complete**: "You're all set!" with button to enter the app.

### 8.10 Notifications

Use `Microsoft.Windows.AppNotifications` (Windows App SDK toast notifications). Show toast for new messages when the app is not focused or the chat is not active. Notification payload should include sender name, message preview, and conversation GUID for deep-linking.

On click: bring app to foreground and navigate to the relevant conversation.

---

## 9. NuGet Dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.WindowsAppSDK` | WinUI 3 framework |
| `Microsoft.Windows.SDK.BuildTools` | Windows SDK |
| `CommunityToolkit.Mvvm` | ObservableObject, RelayCommand, messaging |
| `CommunityToolkit.WinUI.Controls.SettingsControls` | SettingsCard, SettingsExpander |
| `CommunityToolkit.WinUI.Animations` | Implicit animations |
| `Microsoft.Extensions.DependencyInjection` | DI container |
| `Microsoft.Extensions.Http` | HttpClientFactory |
| `System.Text.Json` | JSON serialization |
| `SocketIOClient` | Socket.IO client for real-time events |
| `Microsoft.Data.Sqlite` or `Microsoft.EntityFrameworkCore.Sqlite` | Local message/FCM data cache |
| `Microsoft.Web.WebView2` | Google OAuth consent screen (included in Windows App SDK but may need explicit reference) |
| `Microsoft.Windows.AppNotifications` | Toast notifications (part of WindowsAppSDK) |

---

## 10. Key Implementation Notes

### 10.1 Thread Safety

All Socket.IO event handlers run on a background thread. Any UI property updates must dispatch via:

```csharp
_dispatcherQueue.TryEnqueue(() => { /* update ObservableCollection, etc */ });
```

### 10.2 tempGuid for Message Deduplication

When sending a message, generate a `tempGuid` (use `Guid.NewGuid().ToString()`). The server echoes it back. Use this to match the server response to the locally-created optimistic message and avoid duplicates in the UI.

### 10.3 Attachment Downloads

Download attachments lazily. Show a placeholder (blurhash if available, otherwise file-type icon) until the user scrolls to the message. Cache downloaded files locally in `ApplicationData.Current.LocalFolder`. Use a keyed semaphore to prevent duplicate downloads.

### 10.4 Contact Resolution

The server provides handles (phone numbers, emails). The client must resolve these to display names via the `/contact` and `/contact/query` endpoints. Cache the contact map locally. Fallback: show the raw address.

### 10.5 Date Formatting

Server timestamps are milliseconds since Unix epoch. Convert to `DateTimeOffset.FromUnixTimeMilliseconds()`. Display using:
- Today: time only ("2:30 PM")
- Yesterday: "Yesterday"
- This week: day name ("Tuesday")
- Older: short date ("5/14/26")

Respect the user's 24-hour format setting.

---

## 11. Out of Scope (v1)

These features exist in the Flutter client but are deferred for the initial WinUI3 build:

- FindMy devices/friends map view
- FaceTime call answering via the client
- Scheduled messages UI
- Settings/theme backup sync
- Sticker and Digital Touch rendering
- Audio message recording and playback
- Message effects (screen effects like confetti, laser, etc.)

Implement the data layer for all of these (the API methods exist in the service layer). The UI can be added later.
