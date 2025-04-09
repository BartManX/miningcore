using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Hosting;
using MimeKit;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Messaging;
using Miningcore.Notifications.Messages;
using Miningcore.Pushover;
using NLog;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Notifications;

public class NotificationService : BackgroundService
{
    public NotificationService(
        ClusterConfig clusterConfig,
        PushoverClient pushoverClient,
        IMessageBus messageBus)
    {
        Contract.RequiresNonNull(clusterConfig);
        Contract.RequiresNonNull(messageBus);

        this.clusterConfig = clusterConfig;
        emailSenderConfig = clusterConfig.Notifications.Email;
        this.messageBus = messageBus;
        this.pushoverClient = pushoverClient;

        poolConfigs = clusterConfig.Pools.ToDictionary(x => x.Id, x => x);

        adminEmail = clusterConfig.Notifications?.Admin?.EmailAddress;
    }

    private readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private readonly ClusterConfig clusterConfig;
    private readonly Dictionary<string, PoolConfig> poolConfigs;
    private readonly string adminEmail;
    private readonly IMessageBus messageBus;
    private readonly EmailSenderConfig emailSenderConfig;
    private readonly PushoverClient pushoverClient;

    public string FormatAmount(decimal amount, string poolId)
    {
        return $"{amount:0.#####} {poolConfigs[poolId].Template.Symbol}";
    }

private async Task OnAdminNotificationAsync(AdminNotification notification, CancellationToken ct)
{
    var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
    
    var htmlBody = $@"
        <html>
            <body style='font-family: Arial, sans-serif; font-size: 14px; color: #333;'>
                <h2 style='color: #2c3e50;'>Admin Notification</h2>
                <p><strong>Time:</strong> {timestamp}</p>
                <p><strong>Subject:</strong> {notification.Subject}</p>
                <hr />
                <p>{notification.Message}</p>
                <hr />
                <p style='font-size: 12px; color: #999;'>This message was automatically sent by the Miningcore notification system.</p>
            </body>
        </html>";

    if(!string.IsNullOrEmpty(adminEmail))
        await Guard(() => SendEmailAsync(adminEmail, notification.Subject, htmlBody, ct), LogGuarded);

    if(clusterConfig.Notifications?.Pushover?.Enabled == true)
        await Guard(() => pushoverClient.PushMessage(notification.Subject, notification.Message, PushoverMessagePriority.None, ct), LogGuarded);
}


private async Task OnBlockFoundNotificationAsync(BlockFoundNotification notification, CancellationToken ct)
{
    var pool = poolConfigs[notification.PoolId];
    var coin = pool.Template;

    var subject = $"Pool: {coin.Name} - Block Found Notification";

    var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";

    // Optional: link to block explorer if available
    string blockLink = !string.IsNullOrEmpty(coin.ExplorerBlockLink)
        ? $"<a href='{string.Format(coin.ExplorerBlockLink, notification.BlockHeight)}' target='_blank'>View Block</a>"
        : $"Block #{notification.BlockHeight}";

    var message = $@"
        <h2>🎉 New Block Found!</h2>
        <p><strong>Time:</strong> {timestamp}</p>
        <p><strong>Coin:</strong> {coin.Name} ({coin.Symbol})</p>
        <p><strong>Pool ID:</strong> {notification.PoolId}</p>
        <p><strong>Block Height:</strong> {blockLink}</p>
        <p>The pool has found a new block candidate. Miners, keep those hashrates up! 💪</p>
    ";

    if(clusterConfig.Notifications?.Admin?.NotifyBlockFound == true)
    {
        await Guard(() => SendEmailAsync(adminEmail, subject, message, ct), LogGuarded);

        if(clusterConfig.Notifications?.Pushover?.Enabled == true)
            await Guard(() => pushoverClient.PushMessage(subject, $"Pool {notification.PoolId} found block candidate {notification.BlockHeight}", PushoverMessagePriority.None, ct), LogGuarded);
    }
}


private async Task OnPaymentNotificationAsync(PaymentNotification notification, CancellationToken ct)
{
    var now = DateTime.UtcNow;
    var coin = poolConfigs[notification.PoolId].Template;

    if(string.IsNullOrEmpty(notification.Error))
    {
        // prepare tx links
        var txLinks = Array.Empty<string>();

        if(!string.IsNullOrEmpty(coin.ExplorerTxLink))
            txLinks = notification.TxIds.Select(txHash => string.Format(coin.ExplorerTxLink, txHash)).ToArray();

        var explorerLinks = txLinks.Length > 0
            ? string.Join("<br>", txLinks.Select(x => $"<a href=\"{x}\" target=\"_blank\">{x}</a>"))
            : "N/A";

        const string subject = "Payout Success Notification";

        var message = $@"
            <html>
                <body style=""font-family: sans-serif;"">
                    <h2 style=""color: #4CAF50;"">✅ Payout Success</h2>
                    <p><strong>Time (UTC):</strong> {now}</p>
                    <p><strong>Pool:</strong> {notification.PoolId}</p>
                    <p><strong>Coin:</strong> {coin.Name} ({coin.Symbol})</p>
                    <p><strong>Total Paid:</strong> {FormatAmount(notification.Amount, notification.PoolId)}</p>
                    <p><strong>Recipients:</strong> {notification.RecipientsCount}</p>
                    <p><strong>Transaction(s):</strong><br>{explorerLinks}</p>
                </body>
            </html>";

        if(clusterConfig.Notifications?.Admin?.NotifyPaymentSuccess == true)
        {
            await Guard(() => SendEmailAsync(adminEmail, subject, message, ct), LogGuarded);

            if(clusterConfig.Notifications?.Pushover?.Enabled == true)
                await Guard(() => pushoverClient.PushMessage(subject, $"Paid {FormatAmount(notification.Amount, notification.PoolId)} from pool {notification.PoolId}", PushoverMessagePriority.None, ct), LogGuarded);
        }
    }

    else
    {
        var symbol = poolConfigs[notification.PoolId].Template.Symbol;
        const string subject = "Payout Failure Notification";

        var message = $@"
            <html>
                <body style=""font-family: sans-serif;"">
                    <h2 style=""color: #f44336;"">❌ Payout Failed</h2>
                    <p><strong>Time (UTC):</strong> {now}</p>
                    <p><strong>Pool:</strong> {notification.PoolId}</p>
                    <p><strong>Amount:</strong> {notification.Amount} {symbol}</p>
                    <p><strong>Error:</strong> {notification.Error}</p>
                </body>
            </html>";

        await Guard(() => SendEmailAsync(adminEmail, subject, message, ct), LogGuarded);

        if(clusterConfig.Notifications?.Pushover?.Enabled == true)
            await Guard(() => pushoverClient.PushMessage(subject, message, PushoverMessagePriority.None, ct), LogGuarded);
    }
}


    public async Task SendEmailAsync(string recipient, string subject, string body, CancellationToken ct)
    {
        logger.Info(() => $"Sending '{subject.ToLower()}' email to {recipient}");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(emailSenderConfig.FromName, emailSenderConfig.FromAddress));
        message.To.Add(new MailboxAddress("", recipient));
        message.Subject = subject;
        message.Body = new TextPart("html") { Text = body };

        using(var client = new SmtpClient())
        {
            await client.ConnectAsync(emailSenderConfig.Host, emailSenderConfig.Port, cancellationToken: ct);
            await client.AuthenticateAsync(emailSenderConfig.User, emailSenderConfig.Password, ct);
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);
        }

        logger.Info(() => $"Sent '{subject.ToLower()}' email to {recipient}");
    }

    private void LogGuarded(Exception ex)
    {
        logger.Error(ex);
    }

    private IObservable<IObservable<Unit>> Subscribe<T>(Func<T, CancellationToken, Task> handler, CancellationToken ct)
    {
        return messageBus.Listen<T>()
            .Select(msg => Observable.FromAsync(() =>
                Guard(()=> handler(msg, ct), LogGuarded)));
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var obs = new List<IObservable<IObservable<Unit>>>();

        if(clusterConfig.Notifications?.Admin?.Enabled == true)
        {
            obs.Add(Subscribe<AdminNotification>(OnAdminNotificationAsync, ct));
            obs.Add(Subscribe<BlockFoundNotification>(OnBlockFoundNotificationAsync, ct));
            obs.Add(Subscribe<PaymentNotification>(OnPaymentNotificationAsync, ct));
        }

        if(obs.Count > 0)
        {
            await obs
                .Merge()
                .ObserveOn(TaskPoolScheduler.Default)
                .Concat()
                .ToTask(ct);
        }
    }
}
