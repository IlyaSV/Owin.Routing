﻿using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Owin;
using Newtonsoft.Json;

namespace Owin.Routing
{
	public static partial class RoutingApi
	{
		/// <summary>
		/// Registers methods annotated with <see cref="RouteAttribute"/> into routing pipeline.
		/// </summary>
		/// <typeparam name="T">Type to reflect.</typeparam>
		/// <param name="app">The OWIN pipeline builder.</param>
		public static IAppBuilder UseApi<T>(this IAppBuilder app)
		{
			var init = DependencyInjection.CompileInitializer<T>();
			return app.UseApi(init);
		}

		/// <summary>
		/// Registers methods annotated with <see cref="RouteAttribute"/> into routing pipeline.
		/// </summary>
		/// <typeparam name="T">Type to reflect.</typeparam>
		/// <param name="app">The OWIN pipeline builder.</param>
		/// <param name="getInstance">Function to get instance of T.</param>
		public static IAppBuilder UseApi<T>(this IAppBuilder app, Func<IOwinContext, T> getInstance)
		{
			return app.Route(route => route.UseApi(getInstance));
		}

		/// <summary>
		/// Registers methods annotated with <see cref="RouteAttribute"/> into routing pipeline.
		/// </summary>
		/// <typeparam name="T">Type to reflect.</typeparam>
		/// <param name="routeBuilder">The fluent API to register http method handlers.</param>
		public static MapRouteBuilder UseApi<T>(this MapRouteBuilder routeBuilder)
		{
			var init = DependencyInjection.CompileInitializer<T>();
			return routeBuilder.UseApi(init);			
		}

		/// <summary>
		/// Registers methods annotated with <see cref="RouteAttribute"/> into routing pipeline.
		/// </summary>
		/// <typeparam name="T">Type to reflect.</typeparam>
		/// <param name="mapRouteBuilder">The fluent API to register http method handlers.</param>
		/// <param name="getInstance">Function to get instance of T.</param>
		/// <returns></returns>
		public static MapRouteBuilder UseApi<T>(this MapRouteBuilder mapRouteBuilder, Func<IOwinContext, T> getInstance)
		{
			if (getInstance == null) throw new ArgumentNullException("getInstance");

			var type = typeof(T);
			var prefixAttr = type.GetAttribute<RoutePrefixAttribute>();
			var prefix = prefixAttr != null ? prefixAttr.Prefix : string.Empty;

			var serializerSettings = type.GetProperties(BindingFlags.Static | BindingFlags.FlattenHierarchy | BindingFlags.Public)
				.Where(p => p.HasAttribute<ResponseSerializerSettingsAttribute>() && p.PropertyType == typeof(JsonSerializerSettings))
				.Select(p => p.GetValue(null) as JsonSerializerSettings)
				.FirstOrDefault();

			var errorHandler = type.GetMethods(BindingFlags.Static | BindingFlags.FlattenHierarchy | BindingFlags.Public)
				.Where(m => m.HasAttribute<ErrorHandlerAttribute>())
				.Select(m => DynamicMethods.CompileMethod(type, m))
				.FirstOrDefault();

			const BindingFlags bindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

			var actions = (from m in type.GetMethods(bindingFlags)
						   let route = m.GetAttribute<RouteAttribute>()
						   where route != null
						   select new { Method = m, Route = route }).ToList();

			actions.ForEach(a =>
			{
				var invoke = DynamicMethods.CompileMethod(type, a.Method);
				var mapper = ParameterMapper.Build(a.Method);
				var returnType = a.Method.ReturnType;

				var verb = GetHttpMethod(a.Method);
				var pattern = AddPrefix(prefix, a.Route.Template);

				mapRouteBuilder.Register(pattern, verb, async ctx =>
				{
					Exception error;
					var args = MapParameters(ctx, mapper, out error);
					if (error != null)
					{
						if (null != errorHandler)
						{
							var errorResponse = errorHandler(null, new object[] { ctx, error });
							if (null != errorResponse)
								await ctx.WriteJson(errorResponse);
						}
						else
						{
							ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
							await ctx.WriteJson(new { error = error.Message });
						}
						return;
					}

					var instance = a.Method.IsStatic ? (object)null : getInstance(ctx);
					var result = invoke(instance, args);

					var task = result as Task;
					if (null != task)
					{
						result = await HandleAsyncResult(task, returnType);
					}

					if (result != null)
					{
						await ctx.WriteJson(result, serializerSettings);
					}
				});
			});

			return mapRouteBuilder;
		}

		private static IAppBuilder UseRoute(this IAppBuilder app, MapRouteBuilder mapRouteBuilder)
		{
			app.Use(async (ctx, next) =>
			{
				var handler = mapRouteBuilder.GetHandler(ctx);
				if (handler != null)
					await handler(ctx);
				else
					await next();
			});

			return app;			
		}

		private static async Task<object> HandleAsyncResult(Task task, Type taskType)
		{
			var hasResult = taskType.IsGenericType && taskType.GetGenericTypeDefinition() == typeof(Task<>);

			if (!hasResult)
			{
				await task;
				return null;
			}

			if (taskType.GetGenericArguments().All(argType => argType.IsArray ? argType.GetElementType().IsPublic : argType.IsPublic))
				return await (dynamic) task;

			return await task.ContinueWith(t =>
			{
				var resultProp = task.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
				return resultProp.GetValue(t);
			});
		}

		private static object[] MapParameters(IOwinContext ctx, Func<IOwinContext, object[]> mapper, out Exception error)
		{
			error = null;
			try
			{
				return mapper(ctx);
			}
			catch (Exception e)
			{
				error = e;
				return null;
			}
		}

		private static string AddPrefix(string prefix, string pattern)
		{
			if (string.IsNullOrEmpty(prefix)) return pattern;
			return prefix.TrimEnd('/') + '/' + pattern.TrimStart('/');
		}

		private static string GetHttpMethod(MethodInfo method)
		{
			var attr = method.GetAttribute<HttpMethodAttribute>();
			if (attr != null)
			{
				return attr.Method;
			}

			var name = method.Name;
			if (name.StartsWith("Create", StringComparison.OrdinalIgnoreCase)
				|| name.StartsWith("Add", StringComparison.OrdinalIgnoreCase)
				|| name.StartsWith("Insert", StringComparison.OrdinalIgnoreCase))
			{
				return HttpMethod.Post;
			}

			if (name.StartsWith("Update", StringComparison.OrdinalIgnoreCase))
			{
				return HttpMethod.Put;
			}

			if (name.StartsWith("Patch", StringComparison.OrdinalIgnoreCase))
			{
				return HttpMethod.Patch;
			}

			if (name.StartsWith("Remove", StringComparison.OrdinalIgnoreCase)
			    || name.StartsWith("Delete", StringComparison.OrdinalIgnoreCase))
			{
				return HttpMethod.Delete;
			}

			return HttpMethod.Get;
		}
	}
}
