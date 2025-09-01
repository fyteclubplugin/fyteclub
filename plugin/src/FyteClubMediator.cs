using System;
using System.Collections.Generic;
using System.Collections.Concurrent;

namespace FyteClub
{
    public class FyteClubMediator
    {
        private readonly ConcurrentQueue<Action> _messageQueue = new();
        private readonly Dictionary<Type, List<object>> _subscribers = new();

        public void Subscribe<T>(object subscriber, Action<T> handler) where T : MessageBase
        {
            if (!_subscribers.ContainsKey(typeof(T)))
                _subscribers[typeof(T)] = new List<object>();
            _subscribers[typeof(T)].Add(new Subscription<T>(subscriber, handler));
        }

        public void Publish<T>(T message) where T : MessageBase
        {
            _messageQueue.Enqueue(() => {
                if (_subscribers.TryGetValue(typeof(T), out var handlers))
                {
                    foreach (var handler in handlers)
                    {
                        if (handler is Subscription<T> sub)
                            sub.Handler(message);
                    }
                }
            });
        }

        public void ProcessQueue()
        {
            while (_messageQueue.TryDequeue(out var action))
                action();
        }

        private class Subscription<T>
        {
            public object Subscriber { get; }
            public Action<T> Handler { get; }
            public Subscription(object subscriber, Action<T> handler)
            {
                Subscriber = subscriber;
                Handler = handler;
            }
        }
    }

    public abstract class MessageBase { }

    public interface IMediatorSubscriber { }

    public class PlayerDetectedMessage : MessageBase
    {
        public string PlayerName { get; set; } = "";
        public nint Address { get; set; }
    }

    public class PlayerRemovedMessage : MessageBase
    {
        public string PlayerName { get; set; } = "";
        public nint Address { get; set; }
    }
}