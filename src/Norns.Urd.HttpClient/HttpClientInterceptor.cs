﻿using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Norns.Urd.DynamicProxy;
using Norns.Urd.Reflection;
using Norns.Urd.Utils;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;

namespace Norns.Urd.Http
{
    public class HttpClientInterceptor : AbstractInterceptor
    {
        private static readonly MethodInfo SerializeAsync = typeof(IHttpClientFactoryHandler).GetMethod(nameof(IHttpClientFactoryHandler.SerializeAsync));
        private static readonly MethodInfo CallDeserialize = typeof(HttpClientInterceptor).GetMethod(nameof(HttpClientInterceptor.Deserialize));
        private static readonly MethodInfo CallDeserializeTaskAsync = typeof(HttpClientInterceptor).GetMethod(nameof(HttpClientInterceptor.DeserializeTaskAsync));
        private static readonly MethodInfo CallDeserializeValueTaskAsync = typeof(HttpClientInterceptor).GetMethod(nameof(HttpClientInterceptor.DeserializeValueTaskAsync));
        private readonly Lazy<IHttpClientFactoryHandler, AspectContext> lazyClientFactory = new Lazy<IHttpClientFactoryHandler, AspectContext>(c => c.ServiceProvider.GetRequiredService<IHttpClientFactoryHandler>());
        private static readonly ConcurrentDictionary<MethodInfo, Func<AspectContext, Task>> asyncCache = new ConcurrentDictionary<MethodInfo, Func<AspectContext, Task>>();

        public override bool CanAspect(MethodReflector method)
        {
            return method.IsDefined<HttpMethodAttribute>();
        }

        public override void Invoke(AspectContext context, AspectDelegate next)
        {
            asyncCache.GetOrAdd(context.Method, CreateHttpClientCaller)(context)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }

        public override async Task InvokeAsync(AspectContext context, AsyncAspectDelegate next)
        {
            await asyncCache.GetOrAdd(context.Method, CreateHttpClientCaller)(context);
        }

        private Func<AspectContext, Task> CreateHttpClientCaller(MethodInfo method)
        {
            var mr = method.GetReflector();
            var tr = method.DeclaringType.GetReflector();
            var clientName = mr.GetCustomAttributesDistinctBy<ClientNameAttribute>(tr)
                .Select(i => i.Name)
                .FirstOrDefault() ?? Options.DefaultName;
            var clientSetters = mr.GetCustomAttributesDistinctBy<ClientSettingsAttribute>(tr).ToArray();
            var requestSetters = mr.GetCustomAttributesDistinctBy<HttpMethodAttribute>(tr)
                .Select(i => i.CreateSettings(mr.Parameters.Where(j => j.IsDefined<RouteAttribute>()), mr.Parameters.Where(j => j.IsDefined<QueryAttribute>())))
                .First()
                .Union(tr.GetCustomAttributes<HttpRequestMessageSettingsAttribute>())
                .Union(mr.GetCustomAttributes<HttpRequestMessageSettingsAttribute>())
                .ToArray();
            var option = mr.GetCustomAttributesDistinctBy<HttpCompletionOptionAttribute>(tr)
                .Select(i => i.Option)
                .FirstOrDefault(HttpCompletionOption.ResponseHeadersRead);
            var contentType = mr.GetCustomAttributesDistinctBy<MediaTypeHeaderValueAttribute>(tr)
                .Select(i => i.ContentType)
                .FirstOrDefault(JsonContentTypeAttribute.Json);
            var bodyHandler = CreateBodyHandler(mr.Parameters.FirstOrDefault(i => i.IsDefined<BodyAttribute>())?.MemberInfo, contentType);
            var returnValueHandler = CreateReturnValueHandler(method);
            var getCancellationToken = mr.CreateCancellationTokenGetter();
            return async (context) =>
            {
                var handler = lazyClientFactory.GetValue(context);
                var client = handler.CreateClient(clientName);
                foreach (var setter in clientSetters)
                {
                    setter.SetClient(client, context);
                }
                var message = new HttpRequestMessage();
                var token = getCancellationToken(context);
                message.Content = await bodyHandler(context, token);
                await handler.SetRequestAsync(message, context, token);
                foreach (var setter in requestSetters)
                {
                    setter.SetRequest(message, context);
                }
                var resp = await client.SendAsync(message, option, token);
                await handler.SetResponseAsync(resp, context, token);
                await returnValueHandler(resp.Content, context, token);
            };
        }

        private Func<HttpContent, AspectContext, CancellationToken, Task> CreateReturnValueHandler(MethodInfo method)
        {
            if (method.IsVoid())
            {
                return (content, context, t) => Task.CompletedTask;
            }
            else if (method.IsReturnValueTask())
            {
                return CreateDeserializeCaller(method.ReturnType.GenericTypeArguments[0], CallDeserializeValueTaskAsync);
            }
            else if (method.IsReturnTask())
            {
                return CreateDeserializeCaller(method.ReturnType.GenericTypeArguments[0], CallDeserializeTaskAsync);
            }
            else if (method.IsValueTask())
            {
                return (content, context, t) => 
                {
                    context.ReturnValue = TaskUtils.CompletedValueTask;
                    return Task.CompletedTask;
                };
            }
            else if (method.IsTask())
            {
                return (content, context, t) =>
                {
                    context.ReturnValue = Task.CompletedTask;
                    return Task.CompletedTask;
                };
            }
            else
            {
                return CreateDeserializeCaller(method.ReturnType, CallDeserialize);
            }
        }

        private Func<HttpContent, AspectContext, CancellationToken, Task> CreateDeserializeCaller(Type returnType, MethodInfo deserializeMethod)
        {
            var caller = deserializeMethod.MakeGenericMethod(returnType)
                .CreateDelegate<Func<HttpClientInterceptor, HttpContent, AspectContext, CancellationToken, Task >>(typeof(Task),
                new Type[] { typeof(HttpClientInterceptor), typeof(HttpContent), typeof(AspectContext), typeof(CancellationToken) },
                (il) =>
                {
                    il.EmitLoadArg(1);
                    il.EmitLoadArg(2);
                    il.EmitLoadArg(3);
                });
            return async (content, context, t) => await caller(this, content, context, t);
        }

        public async Task Deserialize<T>(HttpContent content, AspectContext context, CancellationToken token)
        {
            var result = await lazyClientFactory.GetValue(context).DeserializeAsync<T>(content, token);
            context.ReturnValue = result;
        }

        public async Task DeserializeTaskAsync<T>(HttpContent content, AspectContext context, CancellationToken token)
        {
            var result = await lazyClientFactory.GetValue(context).DeserializeAsync<T>(content, token);
            context.ReturnValue = Task.FromResult(result);
        }

        public async Task DeserializeValueTaskAsync<T>(HttpContent content, AspectContext context, CancellationToken token)
        {
            var result = await lazyClientFactory.GetValue(context).DeserializeAsync<T>(content, token);
            context.ReturnValue = new ValueTask<T>(result);
        }

        private Func<AspectContext, CancellationToken, Task<HttpContent>> CreateBodyHandler(ParameterInfo parameter, MediaTypeHeaderValue contentType)
        {
            if (parameter == null)
            {
                return (c, t) => Task.FromResult<HttpContent>(new StringContent(string.Empty));
            }
            else
            {
                var index = parameter.Position;
                var type = parameter.ParameterType;
                var serializer = SerializeAsync.MakeGenericMethod(type).CreateDelegate<Func<IHttpClientFactoryHandler, AspectContext, CancellationToken, Task<HttpContent>>>(typeof(Task<HttpContent>),
                    new Type[] { typeof(IHttpClientFactoryHandler), typeof(AspectContext), typeof(CancellationToken) },
                    (il) =>
                    {
                        il.EmitLoadArg(1);
                        il.Emit(OpCodes.Call, Constants.GetParameters);
                        il.EmitInt(index);
                        il.Emit(OpCodes.Ldelem_Ref);
                        il.EmitConvertObjectTo(type);
                        il.EmitString(contentType.MediaType);
                        il.EmitLoadArg(2);
                    });
                return async (c, t) => await serializer(lazyClientFactory.GetValue(c), c, t);
            }
        }
    }
}