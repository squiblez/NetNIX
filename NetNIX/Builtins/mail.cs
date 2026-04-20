using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NetNIX.Scripting;

/// <summary>
/// mail — local user messaging.
///
/// Allows local users on the same NetNIX system to send and read
/// short messages, similar to a primitive UNIX mail(1) program.
///
/// Each user has a single mailbox file at /var/mail/&lt;username&gt;
/// (mbox-style). Messages are appended on send and parsed on read.
/// Mailbox files are created on first delivery with permissions
/// rw-rw-rw- so that any local user can deliver a message and the
/// recipient can read or modify (delete) their own mailbox.
///
/// Usage:
///     mail                          List messages in your inbox
///     mail -r N                     Read message number N
///     mail -d N                     Delete message number N
///     mail -s "Subject" user [..]   Send a message (body from stdin/tty)
///     mail -h | --help              Show short usage
/// </summary>
public static class MailCommand
{
    private const string MailDir = "/var/mail";
    private const string Separator = "=== From: ";

    public static int Run(NixApi api, string[] args)
    {
        var argList = args.ToList();

        if (argList.Remove("-h") || argList.Remove("--help"))
        {
            PrintUsage();
            return 0;
        }

        // Setup mode: --setup (root only)
        if (argList.Remove("--setup") || argList.Remove("--init"))
        {
            return SetupMailSystem(api);
        }

        // Send mode: -s "Subject" recipient [recipient ...]
        int sIdx = argList.IndexOf("-s");
        if (sIdx >= 0)
        {
            if (sIdx + 2 >= argList.Count)
            {
                Console.Error.WriteLine("mail: -s requires a subject and at least one recipient");
                return 1;
            }
            string subject = argList[sIdx + 1];
            var recipients = argList.Skip(sIdx + 2).ToList();
            argList.RemoveRange(sIdx, argList.Count - sIdx);
            if (argList.Count > 0)
            {
                Console.Error.WriteLine("mail: unexpected arguments before -s: " + string.Join(' ', argList));
                return 1;
            }
            return SendMail(api, subject, recipients);
        }

        // Read message: -r N
        int rIdx = argList.IndexOf("-r");
        if (rIdx >= 0)
        {
            if (rIdx + 1 >= argList.Count || !int.TryParse(argList[rIdx + 1], out int rn))
            {
                Console.Error.WriteLine("mail: -r requires a message number");
                return 1;
            }
            return ReadMessage(api, rn);
        }

        // Delete message: -d N
        int dIdx = argList.IndexOf("-d");
        if (dIdx >= 0)
        {
            if (dIdx + 1 >= argList.Count || !int.TryParse(argList[dIdx + 1], out int dn))
            {
                Console.Error.WriteLine("mail: -d requires a message number");
                return 1;
            }
            return DeleteMessage(api, dn);
        }

        if (argList.Count > 0)
        {
            Console.Error.WriteLine("mail: unknown argument: " + argList[0]);
            PrintUsage();
            return 1;
        }

        return ListInbox(api);
    }

    // ?? Setup ??????????????????????????????????????????????????????

    private static int SetupMailSystem(NixApi api)
    {
        if (api.Uid != 0)
        {
            Console.Error.WriteLine("mail: --setup requires root privileges");
            return 1;
        }

        Console.WriteLine("Setting up mail system for all users...");
        Console.WriteLine();

        var users = api.GetAllUsers();
        int created = 0;
        int skipped = 0;

        foreach (var (uid, username, gid, home) in users)
        {
            string mbox = MailDir + "/" + username;

            if (api.IsFile(mbox))
            {
                Console.WriteLine($"  {username}: mailbox already exists, skipping");
                skipped++;
                continue;
            }

            // Create empty mailbox
            api.WriteText(mbox, "");

            // Set ownership to the user
            if (!api.Chown(mbox, uid, gid))
            {
                Console.Error.WriteLine($"  {username}: failed to set ownership");
                continue;
            }

            // Set permissions to rw-rw-rw- so anyone can send mail
            if (!api.Chmod(mbox, "rw-rw-rw-"))
            {
                Console.Error.WriteLine($"  {username}: failed to set permissions");
                continue;
            }

            Console.WriteLine($"  {username}: mailbox created");
            created++;
        }

        api.Save();

        Console.WriteLine();
        Console.WriteLine($"Mail system setup complete:");
        Console.WriteLine($"  Created: {created} mailbox{(created == 1 ? "" : "es")}");
        Console.WriteLine($"  Skipped: {skipped} (already exist)");
        Console.WriteLine($"  Total users: {users.Length}");

        return 0;
    }

    // ?? Send ???????????????????????????????????????????????????????

    private static int SendMail(NixApi api, string subject, List<string> recipients)
    {
        // Validate recipients first
        var users = api.GetAllUsers();
        var bad = recipients.Where(r => !users.Any(u => u.username == r)).ToList();
        if (bad.Count > 0)
        {
            foreach (var r in bad)
                Console.Error.WriteLine($"mail: {r}: no such user");
            return 1;
        }

        // Read body from stdin until a single '.' on a line, or EOF.
        Console.WriteLine("(Enter message body. End with a single '.' on a line, or EOF.)");
        var body = new StringBuilder();
        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            if (line == ".") break;
            body.AppendLine(line);
        }

        string from = api.Username;
        string date = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        string sanitizedSubject = subject.Replace('\n', ' ').Replace('\r', ' ');

        var sb = new StringBuilder();
        sb.Append(Separator).Append(from)
          .Append("  Date: ").Append(date)
          .Append("  Subject: ").Append(sanitizedSubject)
          .Append(" ===\n");
        sb.Append(body);
        if (body.Length == 0 || body[body.Length - 1] != '\n')
            sb.Append('\n');
        string entry = sb.ToString();

        int delivered = 0;
        foreach (var rcpt in recipients.Distinct())
        {
            string mbox = MailDir + "/" + rcpt;
            bool existed = api.IsFile(mbox);

            // Check if we can write to the mailbox or its parent directory
            if (existed)
            {
                if (!api.CanWrite(mbox))
                {
                    Console.Error.WriteLine($"mail: {mbox}: Permission denied");
                    continue;
                }
            }
            else
            {
                if (!api.CanWrite(MailDir))
                {
                    Console.Error.WriteLine($"mail: {MailDir}: Permission denied");
                    continue;
                }
            }

            api.AppendText(mbox, entry);

            // If we just created it, set proper ownership and permissions
            // so the recipient can read/delete and future senders can append.
            if (!existed && api.IsFile(mbox))
            {
                // Try to set ownership to recipient (only works if we're root)
                int rcptUid = api.GetUid(rcpt);
                if (rcptUid >= 0 && api.Uid == 0)
                {
                    api.Chown(mbox, rcptUid, rcptUid);
                }
                // Set world-readable/writable permissions so anyone can append
                // and the recipient can read/delete
                api.Chmod(mbox, "rw-rw-rw-");
            }
            delivered++;
        }
        api.Save();

        Console.WriteLine($"mail: delivered to {delivered} recipient{(delivered == 1 ? "" : "s")}.");
        return 0;
    }

    // ?? List ???????????????????????????????????????????????????????

    private static int ListInbox(NixApi api)
    {
        string mbox = MailDir + "/" + api.Username;
        if (!api.IsFile(mbox))
        {
            Console.WriteLine("No mail.");
            return 0;
        }
        var msgs = ParseMailbox(api.ReadText(mbox));
        if (msgs.Count == 0)
        {
            Console.WriteLine("No mail.");
            return 0;
        }
        Console.WriteLine($"Mailbox: {mbox}  ({msgs.Count} message{(msgs.Count == 1 ? "" : "s")})");
        Console.WriteLine("  N  From            Date                  Subject");
        Console.WriteLine("  -  ----            ----                  -------");
        for (int i = 0; i < msgs.Count; i++)
        {
            var m = msgs[i];
            string from = Truncate(m.From, 14);
            string date = Truncate(m.Date, 20);
            string subj = Truncate(m.Subject, 40);
            Console.WriteLine($"  {i + 1,2} {from,-14}  {date,-20}  {subj}");
        }
        Console.WriteLine();
        Console.WriteLine("Use 'mail -r N' to read, 'mail -d N' to delete.");
        return 0;
    }

    // ?? Read ???????????????????????????????????????????????????????

    private static int ReadMessage(NixApi api, int n)
    {
        string mbox = MailDir + "/" + api.Username;
        if (!api.IsFile(mbox))
        {
            Console.WriteLine("No mail.");
            return 0;
        }
        var msgs = ParseMailbox(api.ReadText(mbox));
        if (n < 1 || n > msgs.Count)
        {
            Console.Error.WriteLine($"mail: no message {n} (have {msgs.Count})");
            return 1;
        }
        var m = msgs[n - 1];
        Console.WriteLine($"From:    {m.From}");
        Console.WriteLine($"Date:    {m.Date}");
        Console.WriteLine($"Subject: {m.Subject}");
        Console.WriteLine();
        Console.Write(m.Body);
        if (m.Body.Length == 0 || m.Body[m.Body.Length - 1] != '\n')
            Console.WriteLine();
        return 0;
    }

    // ?? Delete ?????????????????????????????????????????????????????

    private static int DeleteMessage(NixApi api, int n)
    {
        string mbox = MailDir + "/" + api.Username;
        if (!api.IsFile(mbox))
        {
            Console.WriteLine("No mail.");
            return 0;
        }
        var msgs = ParseMailbox(api.ReadText(mbox));
        if (n < 1 || n > msgs.Count)
        {
            Console.Error.WriteLine($"mail: no message {n} (have {msgs.Count})");
            return 1;
        }
        msgs.RemoveAt(n - 1);
        api.WriteText(mbox, SerializeMailbox(msgs));
        api.Save();
        Console.WriteLine($"mail: deleted message {n}.");
        return 0;
    }

    // ?? Parse / serialise ??????????????????????????????????????????

    private sealed class Message
    {
        public string From = "";
        public string Date = "";
        public string Subject = "";
        public string Body = "";
    }

    private static List<Message> ParseMailbox(string text)
    {
        var list = new List<Message>();
        if (string.IsNullOrEmpty(text)) return list;

        var lines = text.Split('\n');
        Message? current = null;
        var bodyBuf = new StringBuilder();

        void Flush()
        {
            if (current != null)
            {
                current.Body = bodyBuf.ToString();
                list.Add(current);
            }
            bodyBuf.Clear();
        }

        foreach (var raw in lines)
        {
            if (raw.StartsWith(Separator) && raw.EndsWith(" ==="))
            {
                Flush();
                current = ParseHeader(raw);
            }
            else if (current != null)
            {
                bodyBuf.Append(raw).Append('\n');
            }
        }
        Flush();

        // Trim trailing newline added by split on the very last line.
        foreach (var m in list)
        {
            if (m.Body.EndsWith("\n\n"))
                m.Body = m.Body.Substring(0, m.Body.Length - 1);
        }
        return list;
    }

    private static Message ParseHeader(string headerLine)
    {
        // === From: alice  Date: 2026-...Z  Subject: hello ===
        var m = new Message();
        string inner = headerLine.Substring(4, headerLine.Length - 4 - 4); // strip "=== " and " ==="
        int dateIdx = inner.IndexOf("  Date: ");
        int subjIdx = inner.IndexOf("  Subject: ");
        if (dateIdx < 0 || subjIdx < 0 || subjIdx < dateIdx) return m;

        m.From = inner.Substring("From: ".Length, dateIdx - "From: ".Length);
        m.Date = inner.Substring(dateIdx + "  Date: ".Length, subjIdx - (dateIdx + "  Date: ".Length));
        m.Subject = inner.Substring(subjIdx + "  Subject: ".Length);
        return m;
    }

    private static string SerializeMailbox(List<Message> msgs)
    {
        var sb = new StringBuilder();
        foreach (var m in msgs)
        {
            sb.Append(Separator).Append(m.From)
              .Append("  Date: ").Append(m.Date)
              .Append("  Subject: ").Append(m.Subject)
              .Append(" ===\n");
            sb.Append(m.Body);
            if (m.Body.Length == 0 || m.Body[m.Body.Length - 1] != '\n')
                sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string Truncate(string s, int len)
    {
        if (s == null) return "";
        if (s.Length <= len) return s;
        return s.Substring(0, Math.Max(0, len - 1)) + "…";
    }

    private static void PrintUsage()
    {
        Console.WriteLine("mail - send and read messages between local users");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  mail                          List messages in your inbox");
        Console.WriteLine("  mail -r N                     Read message number N");
        Console.WriteLine("  mail -d N                     Delete message number N");
        Console.WriteLine("  mail -s \"Subject\" user [..]   Send a message");
        Console.WriteLine("  mail --setup | --init         Initialize mailboxes for all users (root only)");
        Console.WriteLine("  mail -h | --help              Show this help");
        Console.WriteLine();
        Console.WriteLine("When sending, type the body and end with a single '.' on a line.");
        Console.WriteLine("See 'man mail' for full documentation.");
    }
}
