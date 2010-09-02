using System.Configuration;
using System.IO;
using Castle.ActiveRecord;
using Common.Tools;
using Inforoom.Common;
using NUnit.Framework;
using PrgData.Common;
using Test.Support;
using PrgData;
using System;
using System.Text.RegularExpressions;
using MySql.Data.MySqlClient;
using System.Data;
using NHibernate.Criterion;

namespace Integration
{
	[TestFixture]
	public class ImpersonalPriceFixture
	{
		private TestClient client;
		private TestUser user;
		private TestOldClient offersClient;

		private TestSmartOrderRule smartRule;
		private TestDrugstoreSettings orderRuleFuture;
		private TestDrugstoreSettings orderRuleOld;

		TestOldClient oldClient;
		TestOldUser oldUser;

		private uint lastUpdateId;
		private string responce;

		private string UniqueId;

		[SetUp]
		public void Setup()
		{
			UniqueId = "123";
			Test.Support.Setup.Initialize();
			ServiceContext.GetUserHost = () => "127.0.0.1";
			UpdateHelper.GetDownloadUrl = () => "http://localhost/";
			ConfigurationManager.AppSettings["WaybillPath"] = "FtpRoot\\";

			using (var transaction = new TransactionScope())
			{

				var permission = TestUserPermission.ByShortcut("AF");

				var offersRegion = TestRegion.FindFirst(Expression.Like("Name", "���������", MatchMode.Anywhere));
				Assert.That(offersRegion, Is.Not.Null, "�� ����� ������ '�����-���������' ��� offersClient");

				offersClient = TestOldClient.CreateTestClient(offersRegion.Id);

				client = TestClient.CreateSimple();
				user = client.Users[0];

				client.Users.Each(u =>
				                  	{
				                  		u.AssignedPermissions.Add(permission);
				                  		u.SendRejects = true;
				                  		u.SendWaybills = true;
				                  	});
				user.Update();

				oldClient = TestOldClient.CreateTestClient();
				oldUser = oldClient.Users[0];

				smartRule = new TestSmartOrderRule();
				smartRule.OffersClientCode = offersClient.Id;
				smartRule.SaveAndFlush();

				var session = ActiveRecordMediator.GetSessionFactoryHolder().CreateSession(typeof(ActiveRecordBase));
				try
				{
					session.CreateSQLQuery(@"
				insert into usersettings.AssignedPermissions (PermissionId, UserId) values (:permissionid, :userid)")
						.SetParameter("permissionid", permission.Id)
						.SetParameter("userid", oldUser.Id)
						.ExecuteUpdate();
				}
				finally
				{
					ActiveRecordMediator.GetSessionFactoryHolder().ReleaseSession(session);
				}
			}

			using (var transaction = new TransactionScope())
			{
				orderRuleFuture = TestDrugstoreSettings.Find(client.Id);
				orderRuleFuture.SmartOrderRule = smartRule;
				orderRuleFuture.EnableImpersonalPrice = true;
				orderRuleFuture.UpdateAndFlush();

				orderRuleOld = TestDrugstoreSettings.Find(oldClient.Id);
				orderRuleOld.SmartOrderRule = smartRule;
				orderRuleOld.EnableImpersonalPrice = true;
				orderRuleOld.UpdateAndFlush();
			}

			if (Directory.Exists("FtpRoot"))
				FileHelper.DeleteDir("FtpRoot");

			Directory.CreateDirectory("FtpRoot");
		}

		[Test]
		public void Check_update_helper_for_Future()
		{
			CheckUpdateHelper(user.Login);
		}

		[Test]
		public void Check_update_helper_for_old()
		{
			CheckUpdateHelper(oldUser.OSUserName);
		}

		public void CheckUpdateHelper(string login)
		{
			using (var connection = new MySqlConnection(Settings.ConnectionString()))
			{
				connection.Open();

				var updateData = UpdateHelper.GetUpdateData(connection, login);
				var helper = new UpdateHelper(updateData, connection);

				Assert.That(updateData.EnableImpersonalPrice, Is.True, "�� ������� �������� '������������ �����'");
				Assert.That(updateData.OffersClientCode, Is.EqualTo(offersClient.Id), "�� ��������� �� OffersClient");
				Assert.That(updateData.OffersRegionCode, Is.EqualTo(offersClient.RegionCode), "�� ��������� ��� ������� � OffersClient");

				CheckSQL(false, connection, updateData, helper);

				CheckSQL(true, connection, updateData, helper);

				//var selectCommand = new MySqlCommand() { Connection = connection };
				//helper.SetUpdateParameters(selectCommand, true, DateTime.Now.AddDays(-10), DateTime.Now);

				//helper.PrepareImpersonalOffres(selectCommand);
				//selectCommand.CommandText = "select Count(*) from CoreAssortment A WHERE A.CodeFirmCr IS NOT NULL";
				//var countWithProducers = Convert.ToUInt32(selectCommand.ExecuteScalar());
				//selectCommand.CommandText = "select Count(*) from CoreProducts A ";
				//var countProducts = Convert.ToUInt32(selectCommand.ExecuteScalar());

				//Console.WriteLine("Offers count = {0} : withProducers : {1}  Products : {2}", countWithProducers + countProducts, countWithProducers, countProducts);
			}
		}

		private void CheckSQL(bool cumulative, MySqlConnection connection, UpdateData updateData, UpdateHelper helper)
		{
			var selectCommand = new MySqlCommand() { Connection = connection };
			helper.SetUpdateParameters(selectCommand, cumulative, DateTime.Now.AddDays(-10), DateTime.Now);

			CheckFillData(selectCommand, helper.GetSynonymCommand(cumulative), updateData);

			CheckFillData(selectCommand, helper.GetSynonymFirmCrCommand(cumulative), updateData);

			CheckFillData(selectCommand, helper.GetRegionsCommand(), updateData);

			CheckFillData(selectCommand, helper.GetMinReqRuleCommand(), updateData);

			try
			{
				//selectCommand.CommandText =
				//    "drop temporary table if exists UserSettings.Prices; create temporary table UserSettings.Prices ENGINE = MEMORY select ClientCode from usersettings.RetClientsSet limit 1;";
				//selectCommand.ExecuteNonQuery();
				CheckFillData(selectCommand, helper.GetPricesRegionalDataCommand(), updateData);

				CheckFillData(selectCommand, helper.GetRegionalDataCommand(), updateData);
				
				
			}
			finally
			{
				//selectCommand.CommandText =
				//    "drop temporary table if exists UserSettings.Prices;";
				//selectCommand.ExecuteNonQuery();
			}
		}

		private void CheckFillData(MySqlCommand selectCommand, string sqlCommand, UpdateData updateData)
		{
			var dataAdapter = new MySqlDataAdapter(selectCommand);
			selectCommand.CommandText = sqlCommand;
			var dataTable = new DataTable();
			dataAdapter.Fill(dataTable);
			Assert.That(dataTable.Rows.Count, Is.GreaterThan(0), "������ �� ������ ������: {0}", sqlCommand);

			if (dataTable.Columns.Contains("RegionCode"))
			{
				var rows = dataTable.Select("RegionCode = " + updateData.OffersRegionCode);
				Assert.That(rows.Length, Is.EqualTo(dataTable.Rows.Count), 
					"�� ��� ������ � ������� � ������� RegionCode ����� �������� {0}: {1}", updateData.OffersRegionCode, sqlCommand);
			}

			if (dataTable.Columns.Contains("PriceCode"))
			{
				var rows = dataTable.Select("PriceCode = " + updateData.ImpersonalPriceId);
				Assert.That(rows.Length, Is.EqualTo(dataTable.Rows.Count),
					"�� ��� ������ � ������� � ������� PriceCode ����� �������� {0}: {1}", updateData.ImpersonalPriceId, sqlCommand);
			}

		}

		[Test]
		public void Check_GetUserData_for_Future()
		{
			CheckGetUserData(user.Login);
		}

		[Test]
		public void Check_GetUserData_for_Old()
		{
			CheckGetUserData(oldUser.OSUserName);
		}

		[Test]
		public void Check_AnalitFReplicationInfo_after_GetUserData()
		{
			var ExistsFirms = MySqlHelper.ExecuteScalar(
				Settings.ConnectionString(),
				@"
call usersettings.GetPrices2(?OffersClientCode);
select
  count(*)
from
  Prices
  left join usersettings.AnalitFReplicationInfo afi on afi.FirmCode = Prices.FirmCode and afi.UserId = ?UserId
where
  afi.UserId is null;",
				new MySqlParameter("?OffersClientCode", offersClient.Id),
				new MySqlParameter("?UserId", user.Id));

			Assert.That(
				ExistsFirms,
				Is.GreaterThan(0),
				"���� ������ {0} ������ � ������ ������� {1} � ���� � AnalitFReplicationInfo ��������� ��� ����� �� ������� {2}",
				client.Id,
				client.RegionCode,
				offersClient.RegionCode);

			CheckGetUserData(user.Login);

			using (var connection = new MySqlConnection(Settings.ConnectionString()))
			{
				connection.Open();
				MySqlHelper.ExecuteNonQuery(
					connection,
					"call usersettings.GetPrices2(?OffersClientCode)",
					new MySqlParameter("?OffersClientCode", offersClient.Id));

				var nonExistsFirms = MySqlHelper.ExecuteScalar(
					connection,
					@"
select
  count(*)
from
  Prices
  left join usersettings.AnalitFReplicationInfo afi on afi.FirmCode = Prices.FirmCode and afi.UserId = ?UserId
where
  afi.UserId is null",
					new MySqlParameter("?UserId", user.Id));

				Assert.That(
					nonExistsFirms,
					Is.EqualTo(0),
					"� ������� {0} � AnalitFReplicationInfo ������ ���� ��� ����� �� ������� {1}",
					client.Id,
					offersClient.RegionCode);

				var nonExistsForce = MySqlHelper.ExecuteScalar(
					connection,
					@"
select
  count(*)
from
  Prices
  left join usersettings.AnalitFReplicationInfo afi on afi.FirmCode = Prices.FirmCode and afi.UserId = ?UserId
where
    afi.UserId is not null
and afi.ForceReplication = 0",
					new MySqlParameter("?UserId", user.Id));

				Assert.That(
					nonExistsForce,
					Is.EqualTo(0),
					"� ������� {0} � AnalitFReplicationInfo �� ������ ���� ����� � ForceReplication � 0 ��� ���� �� ������� {1}",
					client.Id,
					offersClient.RegionCode);
			}

			CommitExchange();

			var nonExistsForceGt0 = MySqlHelper.ExecuteScalar(
				Settings.ConnectionString(),
				@"
call usersettings.GetPrices2(?OffersClientCode);
select
  count(*)
from
  Prices
  left join usersettings.AnalitFReplicationInfo afi on afi.FirmCode = Prices.FirmCode and afi.UserId = ?UserId
where
    afi.UserId is not null
and afi.ForceReplication > 0",
				new MySqlParameter("?OffersClientCode", offersClient.Id),
				new MySqlParameter("?UserId", user.Id));

			Assert.That(
				nonExistsForceGt0,
				Is.EqualTo(0),
				"� ������� {0} � AnalitFReplicationInfo �� ������ ���� ����� � ForceReplication > 0 ��� ���� �� ������� {1}",
				client.Id,
				offersClient.RegionCode);
		}

		[Test(Description = "�������� �� ������������ ������ ��������� AnalitF")]
		public void CheckBuildNo()
		{
			CheckGetUserData(user.Login);

			var updateTime = CommitExchange();

			var serviceResult = LoadData(false, updateTime, "6.0.7.100");

			Assert.That(serviceResult, Is.StringStarting("Error=������������ ������ ��������� �� ���������").IgnoreCase, "����������� ����� �� �������");
			Assert.That(serviceResult, Is.StringContaining("Desc=������ ������").IgnoreCase, "����������� ����� �� �������");
		}

		private void CheckGetUserData(string login)
		{
			SetCurrentUser(login);
			lastUpdateId = 0;
			SimpleLoadData();
			Assert.That(responce, Is.Not.StringContaining("Error=").IgnoreCase, "����� �� ������� ���������, ��� ������� ������");
			Assert.That(lastUpdateId, Is.GreaterThan(0), "UpdateId �� ����������");
		}

		private void SetCurrentUser(string login)
		{
			ServiceContext.GetUserName = () => login;
		}

		private string SimpleLoadData()
		{
			return LoadData(false, DateTime.Now, "6.0.7.1183");
		}

		private string LoadData(bool getEtalonData, DateTime accessTime, string appVersion)
		{
			var service = new PrgDataEx();
			service.ResultFileName = "results";
			responce = service.GetUserData(accessTime, getEtalonData, appVersion, 50, UniqueId, "", "", false);

			var match = Regex.Match(responce, @"\d+").Value;
			if (match.Length > 0)
				lastUpdateId = Convert.ToUInt32(match);
			return responce;
		}

		private DateTime CommitExchange()
		{
			var service = new PrgDataEx();
			service.ResultFileName = "results";

			var updateTime = service.CommitExchange(lastUpdateId, false);

			var dbUpdateTime = Convert.ToDateTime( MySqlHelper.ExecuteScalar(
				Settings.ConnectionString(),
				@"
select 
  uui.UpdateDate 
from 
  logs.AnalitFUpdates afu
  join usersettings.UserUpdateInfo uui on uui.UserId = afu.UserId
where
  afu.UpdateId = ?UpdateId"
				,
				new MySqlParameter("?UpdateId", lastUpdateId)));

			Assert.That(updateTime, Is.EqualTo(dbUpdateTime.ToUniversalTime()), "�� ��������� ���� ����������, ��������� �� ����, ��� UpdateId: {0}", lastUpdateId);

			return updateTime;
		}
		
	}
}