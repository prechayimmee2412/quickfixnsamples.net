﻿using QuickFix;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks.Dataflow;
using QuickFix.Fields;
using System.Globalization;
using Common;
using Symbol = QuickFix.Fields.Symbol;

namespace ConsoleSample
{
    internal class Program
    {
        private static readonly string _configFile = "Config-dev.cfg";

        private static QuickFix.Transport.SocketInitiator _initiator;
        private static QuickFixNApp _application;

        private static void Main(string[] args)
        {
            if (File.Exists(_configFile) is false)
            {
                Console.WriteLine("Error: Config file not found");
                Environment.Exit(2);
            }

            try
            {
                SessionSettings settings = new(_configFile);

                var defaultSettings = settings.Get();

                var sessionId = settings.GetSessions().First();

                var username = defaultSettings.GetString("Username");
                var password = defaultSettings.GetString("Password");

                _application = new(username, password, sessionId.SenderCompID, sessionId.SenderSubID, sessionId.TargetCompID);
                IMessageStoreFactory storeFactory = new FileStoreFactory(settings);

                _initiator = new(_application, storeFactory, settings);

                _initiator.Start();

                var dataflowLinkOptions = new DataflowLinkOptions { PropagateCompletion = true };

                _ = _application.IncomingMessagesBuffer.LinkTo(new ActionBlock<Message>(message =>
                {
                    var messageType = message.Header.GetString(35);

                    if (messageType.Equals("0", StringComparison.OrdinalIgnoreCase) || messageType.Equals("1", StringComparison.OrdinalIgnoreCase) || messageType.Equals("A", StringComparison.OrdinalIgnoreCase)) return;

                    Console.WriteLine($"\nIncoming:\n{message.GetMessageText()}\n");
                }), dataflowLinkOptions);

                _ = _application.OutgoingMessagesBuffer.LinkTo(new ActionBlock<Message>(message =>
                {
                    var messageType = message.Header.GetString(35);

                    if (messageType.Equals("0", StringComparison.OrdinalIgnoreCase) || messageType.Equals("1", StringComparison.OrdinalIgnoreCase) || messageType.Equals("A", StringComparison.OrdinalIgnoreCase)) return;

                    Console.WriteLine($"\nOutgoing:\n{message.GetMessageText()}\n");
                }), dataflowLinkOptions);

                Run();

                _initiator.Stop();
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                Console.WriteLine(e.StackTrace);
            }

            Environment.Exit(1);
        }

        private static void Run()
        {
            while (true)
            {
                try
                {
                    var cmd = QueryAction();

                    if (cmd is null) continue;

                    var action = cmd[0].ToCharArray()[0];

                    if (action == 'g')
                    {
                        if (_initiator.IsStopped)
                        {
                            Console.WriteLine("Restarting initiator...");
                            _initiator.Start();
                        }
                        else
                            Console.WriteLine("Already started.");
                    }
                    else if (action == 'x')
                    {
                        if (_initiator.IsStopped)
                            Console.WriteLine("Already stopped.");
                        else
                        {
                            Console.WriteLine("Stopping initiator...");
                            _initiator.Stop();
                        }
                    }
                    else if (action is 'q' or 'Q')
                        break;

                    ExecuteAction(action, cmd.Skip(1).ToArray());
                }
                catch (Exception e)
                {
                    Console.WriteLine("Message Not Sent: " + e.Message);
                    Console.WriteLine("StackTrace: " + e.StackTrace);
                }
            }

            Console.WriteLine("Program shutdown.");

            _application.Dispose();

            Environment.Exit(0);
        }

        private static string[] QueryAction()
        {
            Console.WriteLine();
            Console.WriteLine("Fields with * are required and you must provide a value for them");
            Console.WriteLine("You must pass the field values in exact order provided for each command");
            Console.WriteLine("If you are passing value for optional fields you can't skip the other optional fields before it, so you must provide a value for those optional fields too");
            Console.WriteLine("To skip an optional field you can set its value to 0");

            Console.Write("\n"
                + "1) New Order (*clOrdID|*symbolId|*tradeSide (buy/sell)|*orderType (market, limit, stop)|*orderQty|posMaintRptID|price|expireTime|designation, ex: 1|newOrder|1|buy|market|10000)\n"
                + "2) Cancel Order (*origClOrdID|*clOrdID|orderId, ex: 2|OrderClientID|CancelOrder)\n"
                + "3) Replace Order (*origClOrdID|*clOrdID|*orderQty|orderId|price|stopPx|expireTime, ex: 3|OrderClientID|CancelOrder|30000|0|1.27)\n"
                + "4) Subscribe to Market data (*symoldID|*depth (y/n), ex: 4|1|n)\n"
                + "5) Unsubscribe from Market data Market data (*symoldID|*depth (y/n), ex: 5|1|n)\n"
                + "6) Order Mass Status (*massStatusReqID|*massStatusReqType|issueDate, ex: 6|MassStatus|7)\n"
                + "7) Request For Positions (*posReqID|posMaintRptID, ex: 7|Positions)\n"
                + "8) Security List Request (*securityReqID|*securityListRequestType|symbol, ex: 8|symbols|0)\n"
                + "9) Order Status Request (*clOrdID|side, ex: 9|MyOrders)\n"
                + "Q) Quit\n"
                + "Action: "
            );

            var cmd = Console.ReadLine().Trim();

            if (string.IsNullOrWhiteSpace(cmd)) return default;

            var cmdSplit = cmd.Split('|');

            var action = cmdSplit[0];

            HashSet<string> validActions = new("1,2,3,4,5,6,7,8,q,Q,g,x".Split(','));

            return action.Length != 1 || validActions.Contains(action) == false
                ? throw new InvalidOperationException("Invalid action")
                : cmdSplit;
        }

        private static void ExecuteAction(char action, string[] fields)
        {
            switch (action)
            {
                case '1':
                    SendNewOrderSingle(fields);

                    break;

                case '2':
                    SendCancelOrder(fields);

                    break;

                case '3':
                    SendReplaceOrder(fields);

                    break;

                case '4':
                    SendMarketDataRequest(fields, true);

                    break;

                case '5':
                    SendMarketDataRequest(fields, false);

                    break;

                case '6':
                    SendOrderMassStatusRequest(fields);

                    break;

                case '7':
                    SendRequestForPositions(fields);

                    break;

                case '8':
                    SendSecurityListRequest(fields);

                    break;

                case '9':
                    SendOrderStatusRequest(fields);

                    break;
            }
        }

        private static void SendNewOrderSingle(string[] fields)
        {
            var ordType = new OrdType(fields[3].ToLowerInvariant() switch
            {
                "market" => OrdType.MARKET,
                "limit" => OrdType.LIMIT,
                "stop" => OrdType.STOP,
                _ => throw new Exception("unsupported input"),
            });

            var message = new QuickFix.FIX44.NewOrderSingle(
                new ClOrdID(fields[0]),
                new Symbol(fields[1]),
                new Side(fields[2].ToLowerInvariant().Equals("buy", StringComparison.OrdinalIgnoreCase) ? '1' : '2'),
                new TransactTime(DateTime.Now),
                ordType);

            message.Set(new OrderQty(Convert.ToDecimal(fields[4])));

            if (ordType.getValue() != OrdType.MARKET)
            {
                message.Set(new TimeInForce('1'));

                if (fields.Length >= 7)
                {
                    if (ordType.getValue() == OrdType.LIMIT)
                    {
                        message.Set(new Price(Convert.ToDecimal(fields[6])));
                    }
                    else
                    {
                        message.Set(new StopPx(Convert.ToDecimal(fields[6])));
                    }
                }

                if (fields.Length >= 8 && DateTime.TryParse(fields[7], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiryTime))
                {
                    message.Set(new ExpireTime(expiryTime));
                }
            }
            else
            {
                message.Set(new TimeInForce('3'));

                if (fields.Length >= 6)
                {
                    message.SetField(new StringField(721, fields[5]));
                }
            }

            if (fields.Length >= 9 && string.IsNullOrWhiteSpace(fields[8]) is false)
            {
                message.Set(new Designation(fields[8]));
            }

            _application.SendMessage(message);
        }

        private static void SendCancelOrder(string[] fields)
        {
            QuickFix.FIX44.OrderCancelRequest message = new()
            {
                OrigClOrdID = new OrigClOrdID(fields[0].Trim()),
                ClOrdID = new ClOrdID(fields[1].Trim())
            };

            if (fields.Length >= 3 && fields[2].Trim().Equals("0", StringComparison.OrdinalIgnoreCase) is false)
            {
                message.OrderID = new OrderID(fields[2].Trim());
            }

            _application.SendMessage(message);
        }

        private static void SendReplaceOrder(string[] fields)
        {
            QuickFix.FIX44.OrderCancelReplaceRequest message = new()
            {
                OrigClOrdID = new OrigClOrdID(fields[0].Trim()),
                ClOrdID = new ClOrdID(fields[1].Trim()),
                OrderQty = new OrderQty(Convert.ToDecimal(fields[2].Trim()))
            };

            if (fields.Length >= 4 && fields[3].Trim().Equals("0", StringComparison.OrdinalIgnoreCase) is false)
            {
                message.OrderID = new OrderID(fields[3].Trim());
            }

            if (fields.Length >= 5 && decimal.TryParse(fields[4].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var price) && price != default)
            {
                message.Price = new Price(price);
            }

            if (fields.Length >= 6 && decimal.TryParse(fields[5].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var stopPx) && stopPx != default)
            {
                message.StopPx = new StopPx(stopPx);
            }

            if (fields.Length >= 7 && DateTime.TryParse(fields[6], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiryTime))
            {
                message.Set(new ExpireTime(expiryTime));
            }

            _application.SendMessage(message);
        }

        private static void SendMarketDataRequest(string[] fields, bool subscribe)
        {
            MDReqID mdReqID = new("MARKETDATAID");
            SubscriptionRequestType subType = new(subscribe ? '1' : '2');
            MarketDepth marketDepth = new(fields[1].ToLowerInvariant().Equals("y", StringComparison.OrdinalIgnoreCase) ? 0 : 1);

            QuickFix.FIX44.MarketDataRequest.NoMDEntryTypesGroup bidMarketDataEntryGroup = new() { MDEntryType = new MDEntryType('0') };
            QuickFix.FIX44.MarketDataRequest.NoMDEntryTypesGroup offerMarketDataEntryGroup = new() { MDEntryType = new MDEntryType('1') };

            QuickFix.FIX44.MarketDataRequest.NoRelatedSymGroup symbolGroup = new() { Symbol = new Symbol(fields[0]), };

            QuickFix.FIX44.MarketDataRequest message = new(mdReqID, subType, marketDepth);

            message.AddGroup(bidMarketDataEntryGroup);
            message.AddGroup(offerMarketDataEntryGroup);
            message.AddGroup(symbolGroup);

            _application.SendMessage(message);
        }

        private static void SendOrderMassStatusRequest(string[] fields)
        {
            QuickFix.FIX44.OrderMassStatusRequest message = new(new MassStatusReqID(fields[0]), new MassStatusReqType(Convert.ToInt32(fields[1])));

            if (fields.Length >= 3)
            {
                message.IssueDate = new IssueDate(fields[2]);
            }

            _application.SendMessage(message);
        }

        private static void SendRequestForPositions(string[] fields)
        {
            QuickFix.FIX44.RequestForPositions message = new();

            message.PosReqID = new PosReqID(fields[0]);

            if (fields.Length >= 2)
            {
                message.SetField(new StringField(721, fields[1]));
            }

            _application.SendMessage(message);
        }

        private static void SendSecurityListRequest(string[] fields)
        {
            QuickFix.FIX44.SecurityListRequest message = new(new SecurityReqID(fields[0]), new SecurityListRequestType(Convert.ToInt32(fields[1])));

            if (fields.Length >= 3)
            {
                message.Symbol = new Symbol(fields[2]);
            }

            _application.SendMessage(message);
        }

        private static void SendOrderStatusRequest(string[] fields)
        {
            QuickFix.FIX44.OrderStatusRequest message = new()
            {
                ClOrdID = new ClOrdID(fields[0]),
            };

            if (fields.Length >= 2)
            {
                message.Side = new Side(fields[1].ToLowerInvariant().Equals("buy", StringComparison.OrdinalIgnoreCase) ? '1' : '2');
            }

            _application.SendMessage(message);
        }
    }
}