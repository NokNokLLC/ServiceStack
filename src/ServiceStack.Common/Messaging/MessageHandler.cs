using System;
using System.Text;
using ServiceStack.Logging;
using ServiceStack.Service;
using ServiceStack.ServiceClient.Web;
using ServiceStack.Text;

namespace ServiceStack.Messaging
{
    /// <summary>
    /// Processes all messages in a Normal and Priority Queue.
    /// Expects to be called in 1 thread. i.e. Non Thread-Safe.
    /// </summary>
    /// <typeparam name="T"></typeparam>
	public class MessageHandler<T>
		: IMessageHandler, IDisposable
	{
		private static readonly ILog Log = LogManager.GetLogger(typeof(MessageHandler<T>));

		public const int DefaultRetryCount = 2; //Will be a total of 3 attempts
		private readonly IMessageService messageService;
		private readonly Func<IMessage<T>, object> processMessageFn;
		private readonly Action<IMessage<T>, Exception> processInExceptionFn;
		public Func<string, IOneWayClient> ReplyClientFactory { get; set; }
		private readonly int retryCount;

    	public int TotalMessagesProcessed { get; private set; }
		public int TotalMessagesFailed { get; private set; }
		public int TotalRetries { get; private set; }
		public int TotalNormalMessagesReceived { get; private set; }
		public int TotalPriorityMessagesReceived { get; private set; }
		public int TotalOutMessagesReceived { get; private set; }

		public MessageHandler(IMessageService messageService,
			Func<IMessage<T>, object> processMessageFn)
			: this(messageService, processMessageFn, null, DefaultRetryCount) {}

		private IMessageQueueClient MqClient { get; set; }
		//private Message<T> Message { get; set; }

		public MessageHandler(IMessageService messageService,
			Func<IMessage<T>, object> processMessageFn,
			Action<IMessage<T>, Exception> processInExceptionFn,
			int retryCount)
		{
			if (messageService == null)
				throw new ArgumentNullException("messageService");

			if (processMessageFn == null)
				throw new ArgumentNullException("processMessageFn");

			this.messageService = messageService;
			this.processMessageFn = processMessageFn;
			this.processInExceptionFn = processInExceptionFn ?? DefaultInExceptionHandler;
			this.retryCount = retryCount;
			this.ReplyClientFactory = ClientFactory.Create;
		}

		public Type MessageType
		{
			get { return typeof(T); }
		}

		public void Process(IMessageQueueClient mqClient)
		{
			try
			{
				bool hadReceivedMessages;
				do
				{
					hadReceivedMessages = false;

					byte[] messageBytes;
					while ((messageBytes = mqClient.GetAsync(QueueNames<T>.Priority)) != null)
					{
						this.TotalPriorityMessagesReceived++;
						hadReceivedMessages = true;

						var message = messageBytes.ToMessage<T>();
						ProcessMessage(mqClient, message);
					}

					while ((messageBytes = mqClient.GetAsync(QueueNames<T>.In)) != null)
					{
						this.TotalNormalMessagesReceived++;
						hadReceivedMessages = true;

						var message = messageBytes.ToMessage<T>();
						ProcessMessage(mqClient, message);
					}

				} while (hadReceivedMessages);

			}
			catch (Exception ex)
			{
				var lastEx = ex;
				Log.Error("Error serializing message from mq server: " + lastEx.Message, ex);
			}
		}

        public IMessageHandlerStats GetStats()
		{
		    return new MessageHandlerStats(typeof(T).Name,
                TotalMessagesProcessed, TotalMessagesFailed, TotalRetries, 
                TotalNormalMessagesReceived, TotalPriorityMessagesReceived);
		}

		private void DefaultInExceptionHandler(IMessage<T> message, Exception ex)
		{
			Log.Error("Message exception handler threw an error", ex);

			if (!(ex is UnRetryableMessagingException))
			{
				if (message.RetryAttempts < retryCount)
				{
					message.RetryAttempts++;
					this.TotalRetries++;

					message.Error = new MessagingException(ex.Message, ex).ToMessageError();
					MqClient.Publish(QueueNames<T>.In, message.ToBytes());
					return;
				}
			}

			MqClient.Publish(QueueNames<T>.Dlq, message.ToBytes());
		}

		public void ProcessMessage(IMessageQueueClient mqClient, Message<T> message)
		{
			this.MqClient = mqClient;

			try
			{
				var response = processMessageFn(message);
				this.TotalMessagesProcessed++;

				//If there's no response publish the request message to its OutQ
				if (response == null)
				{
					mqClient.Notify(QueueNames<T>.Out, message.ToBytes());
				}
				else
				{
					//If there is a response send it to the typed response OutQ
					var mqName = message.ReplyTo ?? new QueueNames(response.GetType()).In;
					var replyClient = ReplyClientFactory(mqName);
					if (replyClient != null)
					{
						try
						{
							replyClient.SendOneWay(response);
							return;
						}
						catch (Exception ex)
						{
							Log.Error("Could not send response to '{0}' with client '{1}'"
								.Fmt(mqName, replyClient.GetType().Name), ex);
						}
					}

					//Otherwise send to our trusty response Queue (inc if replyClient fails)
					var responseMessage = Message.Create(response);
					responseMessage.ReplyId = message.Id;
					mqClient.Publish(mqName, responseMessage.ToBytes());
				}
			}
			catch (Exception ex)
			{
				try
				{
					TotalMessagesFailed++;
					processInExceptionFn(message, ex);
				}
				catch (Exception exHandlerEx)
				{
					Log.Error("Message exception handler threw an error", exHandlerEx);
				}
			}
		}

		public void Dispose()
		{
			var shouldDispose = messageService as IMessageHandlerDisposer;
			if (shouldDispose != null)
				shouldDispose.DisposeMessageHandler(this);
		}
	}
}