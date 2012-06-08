using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Common.MySql;
using NUnit.Framework;
using PrgData.Common;
using PrgData.Common.Models;
using Test.Support;

namespace Integration.BaseTests
{
	public class BaseExportFixture : PrepareDataFixture
	{
		protected TestUser user;
		protected UpdateData updateData;
		protected Queue<FileForArchive> files;
		protected string archivefile;

		[SetUp]
		public void Setup()
		{
			archivefile = "temp.7z";
			files = new Queue<FileForArchive>();

			user = CreateUser();

			With.Connection(c => {
				updateData = UpdateHelper.GetUpdateData(c, user.Login);
			});
		}

		protected string ReadExportContent(string prefix)
		{
			return File.ReadAllText(Path.Combine(ServiceContext.MySqlSharedExportPath(), prefix + user.Id + ".txt"),
				Encoding.GetEncoding(1251));
		}

		protected void Export<T>()
		{
			With.Connection(c => {
				var export = (BaseExport)Activator.CreateInstance(typeof(T), updateData, c, files);
				export.Export();
				export.ArchiveFiles(Path.GetFullPath(archivefile));
			});
		}
	}
}