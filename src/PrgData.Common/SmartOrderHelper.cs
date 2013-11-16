﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MySql.Data.MySqlClient;
using Common.Models;
using System.IO;
using log4net;
using Inforoom.Common;
using Castle.Windsor;
using Castle.MicroKernel.Registration;
using Common.Models.Repositories;
using NHibernate;
using PrgData.Common.Models;
using PrgData.Common.Repositories;
using SmartOrderFactory;
using SmartOrderFactory.Domain;
using SmartOrderFactory.Repositories;
using NHibernate.Mapping.Attributes;
using With = Common.MySql.With;
using Common.MySql;
using Common.Tools;


namespace PrgData.Common
{
	public class ParseDefectureException : Exception
	{
		public ParseDefectureException(string message, Exception innerException)
			: base(message, innerException)
		{
		}
	}

	public class SmartOrderHelper
	{
#if DEBUG
		public static bool raiseException = false;
		public static bool raiseExceptionOnEmpty = false;
#endif

		private UpdateData _updateData;

		public uint OrderedClientCode { get; private set; }
		public User User { get; private set; }
		public Address Address { get; private set; }

		private string _tmpBatchFolder;
		private string _tmpBatchArchive;

		public string TmpBatchArchiveFileName
		{
			get { return _tmpBatchArchive; }
		}

		private string _tmpBatchFileName;

		public string ExtractBatchFileName
		{
			get { return Path.GetFileName(_tmpBatchFileName); }
		}

		public string BatchReportFileName;
		public string BatchOrderFileName;
		public string BatchOrderItemsFileName;
		public string BatchReportServiceFieldsFileName;

		private SmartOrderBatchHandler _handler;

		private SmartOrderRule _smartOrderRule;
		private OrderRules _orderRule;

		private readonly ILog _log = LogManager.GetLogger(typeof(SmartOrderHelper));

		private uint _maxOrderId;
		private uint _maxOrderListId;
		private uint _maxBatchId;


		public SmartOrderHelper(UpdateData updateData, uint orderedClientCode, uint maxOrderId, uint maxOrderListId, uint maxBatchId)
		{
			_updateData = updateData;
			OrderedClientCode = orderedClientCode;
			_maxOrderId = maxOrderId;
			_maxOrderListId = maxOrderListId;
			_maxBatchId = maxBatchId;

			using (var unitOfWork = new UnitOfWork()) {
				_orderRule = IoC.Resolve<IOrderFactoryRepository>().GetOrderRule(updateData.ClientId);
				if (!_orderRule.EnableSmartOrder)
					throw new UpdateException("Услуга 'АвтоЗаказ' не предоставляется", "Пожалуйста, обратитесь в АК \"Инфорум\".", "Услуга 'АвтоЗаказ' не предоставляется; ", RequestType.Forbidden);

				_smartOrderRule = IoC.Resolve<ISmartOrderFactoryRepository>().GetSmartOrderRule(updateData.ClientId);

				if (_smartOrderRule == null)
					throw new UpdateException("Не настроены правила для автоматического формирования заказа", "Пожалуйста, обратитесь в АК \"Инфорум\".", "Не настроены правила для автоматического формирования заказа; ", RequestType.Forbidden);

				User = IoC.Resolve<IRepository<User>>().Load(_updateData.UserId);
				NHibernateUtil.Initialize(User.AvaliableAddresses);
				if (User.AvaliableAddresses.Count == 0)
					throw new UpdateException("Услуга 'АвтоЗаказ' не предоставляется", "Пожалуйста, обратитесь в АК \"Инфорум\".", "У пользователя нет доступных адресов доставки; ", RequestType.Forbidden);

				Address = User.AvaliableAddresses.FirstOrDefault(a => a.Id == orderedClientCode);
				if (Address == null)
					throw new UpdateException("Услуга 'АвтоЗаказ' не предоставляется", "Пожалуйста, обратитесь в АК \"Инфорум\".", "Пользователю не доступен адрес с кодом {0}; ".Format(orderedClientCode), RequestType.Forbidden);
				NHibernateUtil.Initialize(Address.Users);
			}

			_tmpBatchFolder = Path.GetTempPath() + Path.GetRandomFileName();
			_tmpBatchArchive = _tmpBatchFolder + @"\batch.7z";
			BatchReportFileName = _tmpBatchFolder + @"\BatchReport.txt";
			BatchOrderFileName = _tmpBatchFolder + @"\BatchOrder.txt";
			BatchOrderItemsFileName = _tmpBatchFolder + @"\BatchOrderItems.txt";
			BatchReportServiceFieldsFileName = _tmpBatchFolder + @"\BatchReportServiceFields.txt";
			Directory.CreateDirectory(_tmpBatchFolder);
		}

		public void PrepareBatchFile(string batchFile)
		{
			var batchFileBytes = Convert.FromBase64String(batchFile);
			using (var fileBatch = new FileStream(_tmpBatchArchive, FileMode.CreateNew))
				fileBatch.Write(batchFileBytes, 0, batchFileBytes.Length);

			if (!ArchiveHelper.TestArchive(_tmpBatchArchive))
				throw new Exception("Полученный архив поврежден.");

			var extractDir = Path.GetDirectoryName(_tmpBatchArchive) + "\\BatchExtract";
			if (!Directory.Exists(extractDir))
				Directory.CreateDirectory(extractDir);

			ArchiveHelper.Extract(_tmpBatchArchive, "*.*", extractDir);
			var files = Directory.GetFiles(extractDir);
			if (files.Length == 0)
				throw new Exception("Полученный архив не содержит файлов.");

			_tmpBatchFileName = files[0];
		}


		private void InternalProcessBatchFile()
		{
			using (var stream = new FileStream(_tmpBatchFileName, FileMode.Open)) {
				try {
					_handler = new SmartOrderBatchHandler(User, Address, stream);
				}
				catch (EmptyDefectureException) {
					throw;
				}
				catch (Exception e) {
					throw new ParseDefectureException("Ошибка при разборе дефектуры", e);
				}

				var orders = _handler.ProcessOrderBatch();
				SaveToFile(_handler.OrderBatchItems, orders);
			}
		}

		public void ProcessBatchFile()
		{
#if DEBUG
			if (raiseException)
				throw new Exception("Тестовое исключение при обработке дефектуры");
#endif
			var success = false;
			var errorCount = 0;
			var startTime = DateTime.Now;
			do {
				try {
#if DEBUG
					if (raiseExceptionOnEmpty && errorCount < 2)
						throw new EmptyOffersListException("Тестовое исключение при пустом списке предложений");
#endif
					InternalProcessBatchFile();
					success = true;
				}
				catch (EmptyOffersListException) {
					errorCount++;
					if (errorCount >= 3 || DateTime.Now.Subtract(startTime).TotalMinutes > 2)
						throw;
				}
			} while (!success);
		}

		private string GetCryptCost(MySqlConnection connection, float cost, string sessionKey)
		{
			var t = MySql.Data.MySqlClient.MySqlHelper.ExecuteScalar(
				connection,
				"select cast(AES_ENCRYPT(?Cost, ?SessionKey) as char);",
				new MySqlParameter("?Cost", cost),
				new MySqlParameter("?SessionKey", sessionKey))
				.ToString();
			return MySql.Data.MySqlClient.MySqlHelper.EscapeString(t);
		}

		private uint GetOrderedId(Order order)
		{
			return order.AddressId.HasValue ? order.AddressId.Value : order.ClientCode;
		}

		private void SaveToFile(List<OrderBatchItem> list, List<Order> orders)
		{
			ServiceFieldsToFile();

			var buildOrder = new StringBuilder();
			var buildItems = new StringBuilder();
			var buildReport = new StringBuilder();

			using (var connection = new MySqlConnection(ConnectionHelper.GetConnectionString())) {
				connection.Open();

				foreach (var order in orders) {
					order.RowId = _maxOrderId;
					_maxOrderId++;
					buildOrder.AppendFormat(
						"{0}\t{1}\t{2}\t{3}\n",
						order.RowId,
						GetOrderedId(order),
						order.PriceList.PriceCode,
						order.RegionCode);
					foreach (var item in order.OrderItems) {
						item.RowId = _maxOrderListId;
						_maxOrderListId++;

						var report = list.Find(reportItem => { return reportItem.Item != null && reportItem.Item.OrderItem == item; });

						var cryptCostWithoutDelayOfPayment =
							report.Item.Offer.CostWithoutDelayOfPayment.ToString(CultureInfo.InvariantCulture.NumberFormat);
						var cryptCost = report.Item.Offer.Cost.ToString(CultureInfo.InvariantCulture.NumberFormat);

						buildItems.AppendFormat(
							"{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}\t{8}\t{9}\t{10}\t{11}\t{12}\t{13}\t{14}\t{15}\t{16}\t{17}\t{18}\t{19}\n",
							item.RowId,
							item.Order.RowId,
							GetOrderedId(order),
							item.CoreId,
							item.ProductId,
							item.CodeFirmCr.HasValue ? item.CodeFirmCr.Value.ToString() : "\\N",
							item.SynonymCode.HasValue ? item.SynonymCode.Value.ToString() : "\\N",
							item.SynonymFirmCrCode.HasValue ? item.SynonymFirmCrCode.Value.ToString() : "\\N",
							item.Code,
							item.CodeCr,
							cryptCostWithoutDelayOfPayment,
							cryptCost,
							item.Await ? "1" : "0",
							item.Junk ? "1" : "0",
							item.Quantity,
							item.RequestRatio.HasValue ? item.RequestRatio.Value.ToString() : "\\N",
							item.OrderCost.HasValue ? item.OrderCost.Value.ToString(CultureInfo.InvariantCulture.NumberFormat) : "\\N",
							item.MinOrderCount.HasValue ? item.MinOrderCount.Value.ToString() : "\\N",
							item.OfferInfo.Period,
							item.OfferInfo.ProducerCost.HasValue ? item.OfferInfo.ProducerCost.Value.ToString(CultureInfo.InvariantCulture.NumberFormat) : "\\N");
					}
				}

				foreach (var report in list) {
					var serviceValues = "";
					if (_updateData.BuildNumber > 1271)
						serviceValues = report.ServiceValuesToExport();

					if (report.Item != null) {
						buildReport.AppendFormat(
							"{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}\t{8}\t{9}{10}\n",
							_maxBatchId,
							report.Item.OrderItem != null ? GetOrderedId(report.Item.OrderItem.Order) : OrderedClientCode,
							report.ProductName.ToMySqlExportString(),
							report.ProducerName.ToMySqlExportString(),
							report.Quantity,
							report.CommentToExport(),
							report.Item.OrderItem != null ? report.Item.OrderItem.RowId.ToString() : "\\N",
							(int)report.Item.Status,
							report.Item.ProductId,
							report.Item.CodeFirmCr.HasValue ? report.Item.CodeFirmCr.Value.ToString() : "\\N",
							serviceValues);
					}
					else
						buildReport.AppendFormat(
							"{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t\\N\t\\N\t\\N\t\\N{6}\n",
							_maxBatchId,
							OrderedClientCode,
							report.ProductName.ToMySqlExportString(),
							report.ProducerName.ToMySqlExportString(),
							report.Quantity,
							report.CommentToExport(),
							serviceValues);
					_maxBatchId++;
				}
			}

			File.WriteAllText(BatchReportFileName, buildReport.ToString(), Encoding.GetEncoding(1251));
			File.WriteAllText(BatchOrderFileName, buildOrder.ToString(), Encoding.GetEncoding(1251));
			File.WriteAllText(BatchOrderItemsFileName, buildItems.ToString(), Encoding.GetEncoding(1251));
		}

		private void ServiceFieldsToFile()
		{
			if (_updateData.BuildNumber > 1271) {
				var buildFields = new StringBuilder();

				foreach (var key in _handler.Source.ServiceFields.Keys)
					buildFields.AppendLine(key.ToMySqlExportString());

				File.WriteAllText(BatchReportServiceFieldsFileName, buildFields.ToString(), Encoding.GetEncoding(1251));
			}
		}

		public void DeleteTemporaryFiles()
		{
			if (Directory.Exists(_tmpBatchFolder))
				try {
					Directory.Delete(_tmpBatchFolder, true);
				}
				catch (Exception exception) {
					_log.Error("Ошибка при удалении временнной директории при обработке дефектуры", exception);
				}
		}

		public static void InitializeIoC()
		{
			With.DefaultConnectionStringName = ConnectionHelper.GetConnectionName();
			var sessionFactoryHolder = new SessionFactoryHolder(ConnectionHelper.GetConnectionName());
			sessionFactoryHolder
				.Configuration
				.AddInputStream(HbmSerializer.Default.Serialize(typeof(Client).Assembly))
				.AddInputStream(HbmSerializer.Default.Serialize(typeof(SmartOrderRule).Assembly))
				.AddInputStream(HbmSerializer.Default.Serialize(typeof(AnalitFVersionRule).Assembly));
			IoC.Initialize(new WindsorContainer()
				.Register(
					Component.For<ISessionFactoryHolder>().Instance(sessionFactoryHolder),
					Component.For<RepositoryInterceptor>(),
					Component.For(typeof(IRepository<>)).ImplementedBy(typeof(Repository<>)),
					Component.For<IOrderFactoryRepository>().ImplementedBy<OrderFactoryRepository>(),
					Component.For<IOfferRepository>().ImplementedBy<OfferRepository>(),
					Component.For<ISmartOrderFactoryRepository>().ImplementedBy<SmartOrderFactoryRepository>(),
					Component.For<ISmartOfferRepository>().ImplementedBy<SmartOfferRepository>(),
					Component.For<IOrderFactory>().ImplementedBy<SmartOrderFactory.SmartOrderFactory>(),
					Component.For<IVersionRuleRepository>().ImplementedBy<VersionRuleRepository>()));
		}
	}
}