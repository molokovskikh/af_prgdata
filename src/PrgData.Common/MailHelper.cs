using System;
using System.Net.Mail;
using System.Configuration;
using System.Text;
using log4net;
using System.IO;

namespace PrgData.Common
{
	public class MailHelper
	{
		private static ILog logger = LogManager.GetLogger(typeof(MailHelper));

		public static void Mail(string messageText, string subject, string attachment)
		{
			try
			{
				var MailAddress = new MailAddress("service@analit.net", "������ AF", Encoding.UTF8);
				var message = new MailMessage("service@analit.net", ConfigurationManager.AppSettings["ErrorMail"]);
				var SC = new SmtpClient("box.analit.net");
				message.From = MailAddress;
				message.Subject = subject;
				message.SubjectEncoding = Encoding.UTF8;
				message.BodyEncoding = Encoding.UTF8;
				message.Body = messageText;

				if (!String.IsNullOrEmpty(attachment))
				{
					var stream = new MemoryStream();
					var writer = new StreamWriter(stream);
					writer.Write(attachment);
					writer.Flush();
					stream.Position = 0;
					message.Attachments.Add(new Attachment(stream, "Attachment.txt"));
				}
				SC.Send(message);
			}
			catch (Exception exception)
			{
				logger
					.ErrorFormat(
						"������ ��� �������� ������:{0}\r\n����:{1}\r\n���� ������:{2}",
						exception,
						subject,
						messageText);
			}
		}

		public static void Mail(string messageText, string subject)
		{
			Mail(messageText, subject, null);
		}

		public static void MailErr(uint ClientId, string ErrSource, string ErrDesc)
		{
			Mail(
				"������: " + ClientId + Environment.NewLine + "�������: " + ErrSource + Environment.NewLine + "��������: " + ErrDesc,
				"������ � ������� ���������� ������");
		}
	}
}