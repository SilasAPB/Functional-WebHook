open System
open Microsoft.Data.Sqlite
open System.Net.Http
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Hosting
open Microsoft.AspNetCore.Hosting
open System.Security.Cryptography.X509Certificates

// ── Configuração ──────────────────────────────────────────────────────────────

let secretToken = "meu-token-secreto"
let gatewayUrl  = "http://127.0.0.1:5001"

// "Banco de dados" em memória — Set de transaction_ids já confirmados
let dbPath = "transactions.db"

let initDb () =
    use conn = new SqliteConnection($"Data Source={dbPath}")
    conn.Open()
    let cmd = conn.CreateCommand()
    cmd.CommandText <- """
        CREATE TABLE IF NOT EXISTS transactions (
            transaction_id TEXT PRIMARY KEY,
            status         TEXT NOT NULL,
            reason         TEXT,
            created_at     TEXT NOT NULL
        )"""
    cmd.ExecuteNonQuery() |> ignore
let saveTransaction (txId: string) (status: string) (reason: string option) =
    use conn = new SqliteConnection($"Data Source={dbPath}")
    conn.Open()
    let cmd = conn.CreateCommand()
    cmd.CommandText <- "INSERT OR IGNORE INTO transactions (transaction_id, status, reason, created_at) VALUES ($id, $status, $reason, $ts)"
    cmd.Parameters.AddWithValue("$id",     txId)                                |> ignore
    cmd.Parameters.AddWithValue("$status", status)                              |> ignore
    cmd.Parameters.AddWithValue("$reason", reason |> Option.map box |> Option.defaultValue (box DBNull.Value)) |> ignore
    cmd.Parameters.AddWithValue("$ts",     DateTime.UtcNow.ToString("o"))       |> ignore
    cmd.ExecuteNonQuery() |> ignore

let isConfirmed (txId: string) =
    use conn = new SqliteConnection($"Data Source={dbPath}")
    conn.Open()
    let cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT COUNT(*) FROM transactions WHERE transaction_id = $id"
    cmd.Parameters.AddWithValue("$id", txId) |> ignore
    (cmd.ExecuteScalar() :?> int64) > 0L

// Cliente HTTP compartilhado (boas práticas: não criar um por request)
let httpClient = new HttpClient()

// ── Tipos ─────────────────────────────────────────────────────────────────────

type PaymentPayload =
    { event          : string
      transaction_id : string option
      amount         : string
      currency       : string
      timestamp      : string }

// Resultado de cada validação: ou passa, ou retorna um código + motivo
type ValidationResult =
    | Ok
    | Fail of statusCode: int * reason: string

// ── Funções auxiliares de I/O (efeitos isolados) ──────────────────────────────

let postToGateway (endpoint: string) (txId: string) =
    let body    = JsonSerializer.Serialize({| transaction_id = txId |})
    let content = new StringContent(body, Encoding.UTF8, "application/json")
    httpClient.PostAsync($"{gatewayUrl}/{endpoint}", content) |> ignore

let cancelTransaction  txId = postToGateway "cancelar"  txId
let confirmTransaction txId =
    saveTransaction txId "confirmed" None
    postToGateway "confirmar" txId

let rejectTransaction txId reason =
    saveTransaction txId "rejected" (Some reason)
    cancelTransaction txId

// Funções puras de validação //

let validateToken (token: string option) : ValidationResult =
    match token with
    | None                            -> Fail(403, "invalid token")
    | Some t when t <> secretToken   -> Fail(403, "invalid token")
    | _                               -> Ok

let validatePayloadExists (data: JsonElement option) : ValidationResult =
    match data with
    | None                                              -> Fail(400, "invalid payload")
    | Some el when el.ValueKind <> JsonValueKind.Object -> Fail(400, "invalid payload")
    | _                                                 -> Ok

let validateRequiredFields (data: JsonElement) : ValidationResult =
    let required = ["event"; "amount"; "currency"; "timestamp"]
    required
    |> List.tryFind (fun key ->
        not (data.TryGetProperty(key) |> fst))
    |> function
       | Some missing -> Fail(400, $"missing field: {missing}")
       | None         -> Ok

let validateTransactionIdExists (data: JsonElement) : ValidationResult =
    match data.TryGetProperty("transaction_id") with
    | false, _ -> Fail(400, "missing field: transaction_id")
    | _        -> Ok

let validateNotDuplicated (txId: string) : ValidationResult =
    if isConfirmed txId then Fail(400, "transaction duplicated")
    else Ok

let validateOrder (txId: string) (amount: string) (currency: string) : ValidationResult =
    if amount <> "49.90" || currency <> "BRL" then Fail(400, "mismatch")
    else Ok

// ── Pipeline de validação ─────────────────────────────────────────────────────

// Encadeia validações: para no primeiro Fail
let (>>=) result next =
    match result with
    | Ok      -> next ()
    | Fail _  -> result

// ── Handler do webhook ────────────────────────────────────────────────────────

let handleWebhook (ctx: HttpContext) =
    task {
        // 1. Lê token do header
        let token =
            match ctx.Request.Headers.TryGetValue("X-Webhook-Token") with
            | true, v -> Some (v.ToString())
            | _       -> None

        // 2. Tenta deserializar o body
        let! bodyData =
            task {
                try
                    let! doc = JsonDocument.ParseAsync(ctx.Request.Body)
                    return Some doc.RootElement
                with _ ->
                    return None
            }

        // 3. Helper para escrever resposta JSON
        let respond statusCode (body: obj) =
            ctx.Response.StatusCode      <- statusCode
            ctx.Response.ContentType     <- "application/json"
            let json = JsonSerializer.Serialize(body)
            ctx.Response.WriteAsync(json)

        // 4. Validação do token (sem tx_id ainda, não cancela)
        match validateToken token with
        | Fail(code, reason) ->
            return! respond code {| status = "cancelled"; reason = reason |}
        | Ok ->

        // 5. Validação do payload
        match validatePayloadExists bodyData with
        | Fail(code, reason) ->
            return! respond code {| status = "cancelled"; reason = reason |}
        | Ok ->

        let data = bodyData.Value

        // 6. Valida transaction_id (sem cancelar, só rejeita)
        match validateTransactionIdExists data with
        | Fail(code, reason) ->
            return! respond code {| status = "cancelled"; reason = reason |}
        | Ok ->

        let txId = data.GetProperty("transaction_id").GetString()

        // 7. Demais validações — se falhar, cancela a transação
        let remainingValidations () =
            validateRequiredFields data >>= fun () ->
            validateNotDuplicated txId  >>= fun () ->
            validateOrder txId
                (data.GetProperty("amount").GetString())
                (data.GetProperty("currency").GetString())

        match remainingValidations () with
        | Fail(code, reason) ->
            rejectTransaction txId reason
            return! respond code {| status = "cancelled"; transaction_id = txId; reason = reason |}
        | Ok ->

        // 8. Tudo certo — confirma
        confirmTransaction txId
        return! respond 200 {| status = "confirmed"; transaction_id = txId |}
    }

// ── Entry point ───────────────────────────────────────────────────────────────

[<EntryPoint>]
let main args =
    initDb ()
    let builder = WebApplication.CreateBuilder(args)
    
    builder.WebHost
           .UseKestrel(fun options ->
               options.ListenLocalhost(5000)
               options.ListenLocalhost(5443, fun listenOptions ->
                    let certPath = $"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}/.aspnet/https/localhost.pem"
                    let keyPath  = $"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}/.aspnet/https/localhost-key.pem"
                    let cert = Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(certPath, keyPath)
                    listenOptions.UseHttps(cert) |> ignore
               )
           ) |> ignore

    let app = builder.Build()
    app.MapPost("/webhook", Func<HttpContext, _>(handleWebhook)) |> ignore
    app.Run()
    0