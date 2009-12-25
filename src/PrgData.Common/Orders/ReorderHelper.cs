﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MySql.Data.MySqlClient;
using Common.MySql;
using System.Data;
using log4net;


namespace PrgData.Common.Orders
{
	public class ReorderHelper : OrderHelper
	{
		private bool _forceSend;
		//Это в старой системе код клиента, а в новой системе код адреса доставки
		private uint _orderedClientCode;
		private List<ClientOrderHeader> _orders = new List<ClientOrderHeader>();

		private bool _calculateLeaders;

		public ReorderHelper(
			UpdateData data, 
			MySqlConnection readOnlyConnection, 
			MySqlConnection readWriteConnection, 
			bool forceSend,
			uint orderedClientCode) :
			base(data, readOnlyConnection, readWriteConnection)
		{
			_forceSend = forceSend;
			_orderedClientCode = orderedClientCode;
		}

		public string PostSameOrders()
		{
			CheckCanPostOrder();

			CheckWeeklySumOrder();

			InternalSendOrders();

			return GetOrdersResult();
		}

		private void InternalSendOrders()
		{
			//Сбрасываем перед заказом
			_orders.ForEach((item) => { item.ClearBeforPost(); });

			//Получить значение флага "Расчитывать лидеров"
			_calculateLeaders = GetCalculateLeaders();

			//делаем проверки минимального заказа
			CheckOrdersByMinRequest();

			//делаем проверку на дублирующиеся заказы
			CheckDuplicatedOrders();

			if (AllOrdersIsSuccess() && !_forceSend)
			{
				//вызываем заполнение таблицы предложений в памяти MySql-сервера
				GetOffers();

				//делаем сравнение с существующим прайсом
				CheckWithExistsPrices();
			}

			if (AllOrdersIsSuccess())
				With.DeadlockWraper(() =>
				{
					var transaction = _readWriteConnection.BeginTransaction();
					try
					{
						//Сбрасываем ServerOrderId перед заказом только у заказов, 
						//которые не являются полностью дублированными
						_orders.ForEach((item) =>
						{
							if (!item.FullDuplicated) 
								item.ServerOrderId = 0; 
						});

						//сохраняем сами заявки в базу
						SaveOrders();

						transaction.Commit();
					}
					catch
					{
						transaction.Rollback();
						throw;
					}
				});
		}

		private void CheckWithExistsPrices()
		{
			foreach (var order in _orders)
			{
				var existCore = new DataTable();
				var dataAdapter = new MySqlDataAdapter(@"
select
  C.Id,
  cast(RIGHT(core.ID, 9) as unsigned) as ClientServerCoreID,
  Core.Cost,
  core.ProductId,
  c.CodeFirmCr,
  c.SynonymCode,
  c.SynonymFirmCrCode,
  c.SynonymCode,
  c.Code,
  c.CodeCr,
  c.Junk,
  c.Await,
  c.Quantity,
  c.RequestRatio,
  c.OrderCost,
  c.MinOrderCount
from
  core,
  farm.core0 c
where
    (c.Id = core.Id)
and (Core.PriceCode = ?PriceCode)
and (Core.RegionCode = ?RegionCode)
				", _connection);
				dataAdapter.SelectCommand.Parameters.AddWithValue("?PriceCode", order.PriceCode);
				dataAdapter.SelectCommand.Parameters.AddWithValue("?RegionCode", order.RegionCode);

				dataAdapter.Fill(existCore);

				foreach (var position in order.Positions)
				{
					if (!position.Duplicated)
					{
						var dataRow = GetDataRowByPosition(existCore, position);
						if (dataRow == null)
							position.SendResult = PositionSendResult.NotExists;
						else
							CheckExistsCorePosition(dataRow, position);
					}
				}

				if (order.Positions.Any((item) => { return item.SendResult != PositionSendResult.Success; }))
					order.SendResult = OrderSendResult.NeedCorrect;
			}
		}

		private void CheckExistsCorePosition(DataRow dataRow, ClientOrderPosition position)
		{
			ushort? serverQuantity = null;
			ushort temp;

			if (!Convert.IsDBNull(dataRow["Quantity"])
				&& !String.IsNullOrEmpty(dataRow["Quantity"].ToString())
				&& ushort.TryParse(dataRow["Quantity"].ToString(), out temp))
			{
				serverQuantity = (ushort?)temp;
			}

			if (!position.Cost.Equals(dataRow["Cost"]))
			{
				position.SendResult = PositionSendResult.DifferentCost;
				position.ServerCost = Convert.ToDecimal(dataRow["Cost"]);
				if (serverQuantity.HasValue)
					position.ServerQuantity = serverQuantity.Value;
			}
			else
				if (serverQuantity.HasValue && (serverQuantity.Value < position.Quantity))
				{
					position.SendResult = PositionSendResult.DifferentQuantity;
					position.ServerCost = Convert.ToDecimal(dataRow["Cost"]);
					position.ServerQuantity = serverQuantity.Value;
				}
		}

		private DataRow GetDataRowByPosition(DataTable existCore, ClientOrderPosition position)
		{
			var offers = existCore.Select("ClientServerCoreID = " + position.ClientServerCoreID);
			if (offers.Length == 1)
				return offers[0];
			else
				if (offers.Length == 0)
				{
					offers = existCore.Select(position.GetFilter());
					if (offers.Length > 0)
						return offers[0];					
				}
				else
					throw new OrderException(String.Format("По ID = {0} нашли больше одной позиции.", position.ClientServerCoreID));

			return null;
		}

		private void SaveOrders()
		{
			foreach (var order in _orders)
			{
				if (!order.FullDuplicated)
				{
					order.ServerOrderId = SaveOrder(
						_orderedClientCode,
						Convert.ToUInt32(order.PriceCode),
						order.RegionCode,
						order.PriceDate,
						order.GetSavedRowCount(),
						Convert.ToUInt32(order.ClientOrderId),
						order.ClientAddition);
					foreach (var position in order.Positions)
						if (!position.Duplicated)
							SaveOrderDetail(order, position);
				}
			}
		}

		private void SaveOrderDetail(ClientOrderHeader order, ClientOrderPosition position)
		{
			var command = new MySqlCommand(@"
 INSERT
 INTO   orders.orderslist
        (
               OrderID          ,
               ProductId        ,
               CodeFirmCr       ,
               SynonymCode      ,
               SynonymFirmCrCode,
               Code             ,
               CodeCr           ,
               Quantity         ,
               Junk             ,
               Await            ,
               Cost             ,
               RequestRatio     ,
               MinOrderCount    ,
               OrderCost        
        )
 SELECT ?OrderID                                      ,
        products.ID                                   ,
        IF(Prod.Id IS NULL, sfcr.codefirmcr, Prod.Id) ,
        syn.synonymcode                               ,
        sfcr.SynonymFirmCrCode                        ,
        ?Code                                         ,
        ?CodeCr                                       ,
        ?Quantity                                     ,
        ?Junk                                         ,
        ?Await                                        ,
        ?Cost                                         ,
        ?RequestRatio                                 ,
        ?MinOrderCount                                ,
        ?OrderCost        
 FROM   catalogs.products
        LEFT JOIN farm.synonym syn
        ON     syn.synonymcode=?SynonymCode
        LEFT JOIN farm.synonymfirmcr sfcr
        ON     sfcr.SynonymFirmCrCode=?SynonymFirmCrCode
        LEFT JOIN catalogs.Producers Prod
        ON     Prod.Id=?CodeFirmCr
 WHERE  products.ID   =?ProductID;"
				, 
				_readWriteConnection);

			command.Parameters.Clear();

			if (_calculateLeaders
				&& (position.MinCost.HasValue || position.LeaderMinCost.HasValue)
				&& (position.MinPriceCode.HasValue || position.LeaderMinPriceCode.HasValue))
			{
				command.CommandText += @"
insert into orders.leaders 
values (last_insert_id(), nullif(?MinCost, 0), nullif(?LeaderMinCost, 0), nullif(?MinPriceCode, 0), nullif(?LeaderMinPriceCode, 0));";
				command.Parameters.AddWithValue("?MinCost", position.MinCost);
				command.Parameters.AddWithValue("?LeaderMinCost", position.LeaderMinCost);
				command.Parameters.AddWithValue("?MinPriceCode", position.MinPriceCode);
				command.Parameters.AddWithValue("?LeaderMinPriceCode", position.LeaderMinPriceCode);
			}

			command.Parameters.AddWithValue("?OrderId", order.ServerOrderId);

			command.Parameters.AddWithValue("?ProductId", position.ProductID);
			command.Parameters.AddWithValue("?CodeFirmCr", position.CodeFirmCr);
			command.Parameters.AddWithValue("?SynonymCode", position.SynonymCode);
			command.Parameters.AddWithValue("?SynonymFirmCrCode", position.SynonymFirmCrCode);
			command.Parameters.AddWithValue("?Code", position.Code);
			command.Parameters.AddWithValue("?CodeCr", position.CodeCr);
			command.Parameters.AddWithValue("?Quantity", position.Quantity);
			command.Parameters.AddWithValue("?Junk", position.Junk);
			command.Parameters.AddWithValue("?Await", position.Await);
			command.Parameters.AddWithValue("?Cost", position.Cost);

			command.Parameters.AddWithValue("?RequestRatio", position.RequestRatio);
			command.Parameters.AddWithValue("?MinOrderCount", position.MinOrderCount);
			command.Parameters.AddWithValue("?OrderCost", position.OrderCost);

			command.ExecuteNonQuery();
		}

		private void GetOffers()
		{
			if (_data.IsFutureClient)
			{
				var command = new MySqlCommand("call future.GetOffers(?UserId)", _connection);
				command.Parameters.AddWithValue("?UserId", _data.UserId);
				command.ExecuteNonQuery();
			}
			else
			{
				var command = new MySqlCommand("call GetOffers(?ClientCode, 0)", _connection);
				command.Parameters.AddWithValue("?clientCode", _data.ClientId);
				command.ExecuteNonQuery();
			}
		}

		private void CheckOrdersByMinRequest()
		{
			//if (_orders.Count > 1)
			//{
			//    _orders[0].SendResult = OrderSendResult.LessThanMinReq;
			//    _orders[0].MinReq = 10000;
			//    _orders[0].ErrorReason = "Поставщик отказал в приеме заказа.\n Сумма заказа меньше минимально допустимой.";
			//}

			foreach (var order in _orders)
			{
				var minReq = GetMinReq(_orderedClientCode, order.RegionCode, Convert.ToUInt32(order.PriceCode));
				order.SendResult = OrderSendResult.Success;
				if ((minReq != null) && minReq.ControlMinReq && (minReq.MinReq > 0))
					if (order.GetSumOrder() < minReq.MinReq)
					{
						order.SendResult = OrderSendResult.LessThanMinReq;
						order.MinReq = minReq.MinReq;
						order.ErrorReason = "Поставщик отказал в приеме заказа.\n Сумма заказа меньше минимально допустимой.";
					}
			}
		}

		private bool GetCalculateLeaders()
		{
			var command = new MySqlCommand("select CalculateLeader from retclientsset where clientcode=?ClientId", _connection);
			command.Parameters.AddWithValue("?ClientId", _data.ClientId);
			return Convert.ToBoolean(command.ExecuteScalar());
		}

		private bool AllOrdersIsSuccess()
		{
			return _orders.All((item) => { return (item.SendResult == OrderSendResult.Success); });
		}

		private string GetOrdersResult()
		{
			var result = String.Empty;

			foreach(var order in _orders)
			{
				if (!order.FullDuplicated && (order.SendResult != OrderSendResult.Unknown))
					if (String.IsNullOrEmpty(result))
						result += order.GetResultToClient();
					else
						result += ";" + order.GetResultToClient();
			}

			return result;
		}

		private void CheckCanPostOrder()
		{
			if (!CanPostOrder(_orderedClientCode))
				throw new OrderUpdateException(
					true,
					5,
					"Отправка заказов запрещена.",
					"Пожалуйста обратитесь в АК \"Инфорум\".");
		}

		private void CheckWeeklySumOrder()
		{
			var WeeklySumOrder = Convert.ToUInt32(MySql.Data.MySqlClient.MySqlHelper
				.ExecuteScalar(
				_connection, @"
SELECT ROUND(IF(SUM(cost            *quantity)>RCS.MaxWeeklyOrdersSum
AND    CheCkWeeklyOrdersSum,SUM(cost*quantity), 0),0)
FROM   orders.OrdersHead Oh,
       orders.OrdersList Ol,
       RetClientsSet RCS
WHERE  WriteTime               >curdate() - interval dayofweek(curdate())-2 DAY
AND    Oh.RowId                =ol.OrderId
AND    RCS.ClientCode          =oh.ClientCode
AND    RCS.CheCkWeeklyOrdersSum=1
AND    RCS.clientcode          = ?ClientCode"
					,
					new MySqlParameter("?ClientCode", _data.ClientId)
											 ));
			if (WeeklySumOrder > 0)
				throw new OrderUpdateException(
					true,
					5,
					String.Format("Превышен недельный лимит заказа (уже заказано на {0} руб).", WeeklySumOrder),
					String.Empty);
		}

		public void ParseOrders(
			ushort orderCount,
			ulong[] clientOrderId,
			ulong[] priceCode,
			ulong[] regionCode,
			DateTime[] priceDate,
			string[] clientAddition,
			ushort[] rowCount,
			ulong[] clientPositionID,
			ulong[] clientServerCoreID,
            ulong[] productID,
            string[] codeFirmCr,
			ulong[] synonymCode,
			string[] synonymFirmCrCode,
            string[] code,
            string[] codeCr, 
            bool[] junk,
            bool[] await,
			string[] requestRatio,
			string[] orderCost,
			string[] minOrderCount,
			ushort[] quantity,
			decimal[] cost, 
            string[] minCost, 
            string[] minPriceCode,
            string[] leaderMinCost, 
            string[] leaderMinPriceCode
			)
		{
			CheckArrayCount(orderCount, clientOrderId.Length, "clientOrderId");
			CheckArrayCount(orderCount, priceCode.Length, "priceCode");
			CheckArrayCount(orderCount, regionCode.Length, "regionCode");
			CheckArrayCount(orderCount, priceDate.Length, "priceDate");
			CheckArrayCount(orderCount, clientAddition.Length, "clientAddition");
			CheckArrayCount(orderCount, rowCount.Length, "rowCount");

			int allPositionCount = rowCount.Sum(item => item);

			CheckArrayCount(allPositionCount, clientPositionID.Length, "clientPositionID");
			CheckArrayCount(allPositionCount, clientServerCoreID.Length, "clientServerCoreID");
			CheckArrayCount(allPositionCount, productID.Length, "productID");
			CheckArrayCount(allPositionCount, codeFirmCr.Length, "codeFirmCr");
			CheckArrayCount(allPositionCount, synonymCode.Length, "synonymCode");
			CheckArrayCount(allPositionCount, synonymFirmCrCode.Length, "synonymFirmCrCode");
			CheckArrayCount(allPositionCount, code.Length, "code");
			CheckArrayCount(allPositionCount, codeCr.Length, "codeCr");
			CheckArrayCount(allPositionCount, junk.Length, "junk");
			CheckArrayCount(allPositionCount, await.Length, "await");
			CheckArrayCount(allPositionCount, requestRatio.Length, "requestRatio");
			CheckArrayCount(allPositionCount, orderCost.Length, "orderCost");
			CheckArrayCount(allPositionCount, minOrderCount.Length, "minOrderCount");
			CheckArrayCount(allPositionCount, quantity.Length, "quantity");

			CheckArrayCount(allPositionCount, cost.Length, "cost");
			CheckArrayCount(allPositionCount, minCost.Length, "minCost");
			CheckArrayCount(allPositionCount, minPriceCode.Length, "minPriceCode");
			CheckArrayCount(allPositionCount, leaderMinCost.Length, "leaderMinCost");
			CheckArrayCount(allPositionCount, leaderMinPriceCode.Length, "leaderMinPriceCode");

			var detailsPosition = 0;
			for (int i = 0; i < orderCount; i++)
			{
				var clientOrder = new ClientOrderHeader()
				{
					ClientOrderId = clientOrderId[i],
					PriceCode = priceCode[i],
					RegionCode = regionCode[i],
					PriceDate = priceDate[i],
					ClientAddition = DecodedDelphiString(clientAddition[i]),
					RowCount = rowCount[i]
				};

				_orders.Add(clientOrder);

				for (int detailIndex = detailsPosition; detailIndex < (detailsPosition + clientOrder.RowCount); detailIndex++)
				{ 
					var position = new ClientOrderPosition()
					{
						ClientPositionID = clientPositionID[detailIndex],
						ClientServerCoreID = clientServerCoreID[detailIndex],
						ProductID = productID[detailIndex],
						CodeFirmCr =
							String.IsNullOrEmpty(codeFirmCr[detailIndex]) ? null : (ulong?)ulong.Parse(codeFirmCr[detailIndex]),
						SynonymCode = synonymCode[detailIndex],
						SynonymFirmCrCode =
							String.IsNullOrEmpty(synonymFirmCrCode[detailIndex]) ? null : (ulong?)ulong.Parse(synonymFirmCrCode[detailIndex]),
						Code = code[detailIndex],
						CodeCr = codeCr[detailIndex],
						Junk = junk[detailIndex],
						Await = await[detailIndex],
						RequestRatio =
							String.IsNullOrEmpty(requestRatio[detailIndex]) ? null : (ushort?)ushort.Parse(requestRatio[detailIndex]),
						OrderCost =
							String.IsNullOrEmpty(orderCost[detailIndex]) ? null : (decimal?)decimal
								.Parse(
									orderCost[detailIndex],
									System.Globalization.NumberStyles.Currency,
									System.Globalization.CultureInfo.InvariantCulture.NumberFormat),
						MinOrderCount =
							String.IsNullOrEmpty(minOrderCount[detailIndex]) ? null : (ulong?)ulong.Parse(minOrderCount[detailIndex]),
						Quantity = quantity[detailIndex],
						Cost = cost[detailIndex],
						MinCost =
							String.IsNullOrEmpty(minCost[detailIndex]) ? null : (decimal?)decimal
								.Parse(
									minCost[detailIndex],
									System.Globalization.NumberStyles.Currency,
									System.Globalization.CultureInfo.InvariantCulture.NumberFormat),
						MinPriceCode =
							String.IsNullOrEmpty(minPriceCode[detailIndex]) ? null : (ulong?)ulong.Parse(minPriceCode[detailIndex]),
						LeaderMinCost =
							String.IsNullOrEmpty(leaderMinCost[detailIndex]) ? null : (decimal?)decimal
								.Parse(
									leaderMinCost[detailIndex],
									System.Globalization.NumberStyles.Currency,
									System.Globalization.CultureInfo.InvariantCulture.NumberFormat),
						LeaderMinPriceCode =
							String.IsNullOrEmpty(leaderMinPriceCode[detailIndex]) ? null : (ulong?)ulong.Parse(leaderMinPriceCode[detailIndex])
					};

					clientOrder.Positions.Add(position);
				}

				detailsPosition += clientOrder.RowCount;
			}
		}

		void CheckArrayCount(int expectedCount, int count, string arrayName)
		{
			if (count != expectedCount)
				throw new OrderException(
					String.Format(
						"В массиве {0} недостаточное кол-во элементов: текущее значение: {1}, необходимое значение: {2}.",
						arrayName,
						count,
						expectedCount));
		}

		public string DecodedDelphiString(string value)
		{
			var result = String.Empty;
			var i = 0;

			while (i < value.Length - 2)
			{
				result += Convert.ToChar(
						Convert.ToByte(
								String.Format(
										"{0}{1}{2}",
										value[i],
										value[i + 1],
										value[i + 2]
								)
						)
				);
				i += 3;
			}

			return result;
		}

		private void CheckDuplicatedOrders()
		{
			ILog _logger = LogManager.GetLogger(this.GetType());

			foreach (var order in _orders)
			{
				//проверку производим только на заказах, которые помечены как успешные
				if (order.SendResult != OrderSendResult.Success)
					continue;

				var existsOrders = new DataTable();
				var dataAdapter = new MySqlDataAdapter(@"
SELECT ol.*
FROM   orders.ordershead oh,
       orders.orderslist ol
WHERE  clientorderid = ?ClientOrderID
AND    writetime    >ifnull(
       (SELECT MAX(requesttime)
       FROM    logs.AnalitFUpdates px
       WHERE   updatetype =2
       AND     px.UserId  = ?UserId
       )
       , now() - interval 2 week)
AND    clientcode= ?ClientCode
AND    ol.orderid=oh.rowid
order by oh.RowId desc
", _connection);
				dataAdapter.SelectCommand.Parameters.AddWithValue("?ClientOrderID", order.ClientOrderId);
				dataAdapter.SelectCommand.Parameters.AddWithValue("?ClientCode", _orderedClientCode);				
				dataAdapter.SelectCommand.Parameters.AddWithValue("?UserId", _data.UserId);

				dataAdapter.Fill(existsOrders);

				if (existsOrders.Rows.Count == 0)
					continue;

				foreach (var position in order.Positions)
				{
					var existsOrderList = existsOrders.Select(position.GetFilterForDuplicatedOrder());
					if (existsOrderList.Length == 1)
					{
						var serverQuantity = Convert.ToUInt16(existsOrderList[0]["Quantity"]);
						//Если меньше или равняется, то считаем, что заказ был уже отправлен
						if (position.Quantity <= serverQuantity)
						{
							position.Duplicated = true;
							_logger.InfoFormat(
								"В новом заказе №{0} (ClientOrderId) от клиента {1} от пользователя {2} "
								+ "удалена дублирующаяся строка с заказом №{3}, строка №{4}",
								order.ClientOrderId,
								_orderedClientCode,
								_data.UserId,
								existsOrderList[0]["OrderId"],
								existsOrderList[0]["RowId"]);
						}
						else
						{
							position.Quantity = (ushort)(position.Quantity - serverQuantity);
							_logger.InfoFormat(
								"В новом заказе №{0} (ClientOrderId) от клиента {1} от пользователя {2} "
								+ "изменено количество товара в связи с дублированием с заказом №{3}, строка №{4}",
								order.ClientOrderId,
								_orderedClientCode,
								_data.UserId,
								existsOrderList[0]["OrderId"],
								existsOrderList[0]["RowId"]);
						}
						//удаляем позицию, чтобы больше не находить ее
						existsOrderList[0].Delete();
					}
					else
						if (existsOrderList.Length > 1)
						{
							var stringBuilder = new StringBuilder();
							stringBuilder.AppendFormat(
								"В новом заказе №{0} (ClientOrderId) от клиента {1} от пользователя {2}"
								+ "поиск вернул несколько позиций: {3}\r\n",
								order.ClientOrderId,
								_orderedClientCode,
								_data.UserId,
								existsOrderList.Length);
							existsOrderList.ToList().ForEach((row) => { stringBuilder.AppendLine(row.ItemArray.ToString()); });
							//Это надо залогировать
						}
				}

				//Если все заказы были помечены как дублирующиеся, то весь заказ помечаем как полностью дублирующийся
				order.FullDuplicated = (order.GetSavedRowCount() == 0);
			}
		}
	}
}