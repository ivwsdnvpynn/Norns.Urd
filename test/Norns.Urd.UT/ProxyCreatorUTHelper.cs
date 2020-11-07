﻿using Microsoft.Extensions.DependencyInjection;
using Norns.Urd.Proxy;

namespace Norns.Urd.UT
{
    public static class ProxyCreatorUTHelper
    {
        public static (IProxyCreator, IInterceptorFactory) InitPorxyCreator()
        {
            var p = new ServiceCollection()
                .AddSingleton<IProxyGenerator, FacadeProxyGenerator>()
                .AddSingleton<IProxyGenerator, InheritProxyGenerator>()
                .AddSingleton<IProxyCreator, ProxyCreator>()
                .AddSingleton<IInterceptorFactory, InterceptorFactory>()
                .AddSingleton<IAspectConfiguration>(new AspectConfiguration())
                .BuildServiceProvider();
            return (p.GetRequiredService<IProxyCreator>(), p.GetRequiredService<IInterceptorFactory>());
        }
    }
}