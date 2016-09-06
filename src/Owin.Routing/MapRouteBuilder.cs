using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Owin;

namespace Owin.Routing
{
	using HandlerFunc = Func<IOwinContext, Task>;

	/// <summary>
	/// Provides fluent API to register http method handlers.
	/// </summary>
	public sealed class MapRouteBuilder
	{
		private class RoutePriorityComparer : IComparer<Route>
		{
			public int Compare(Route x, Route y)
			{
				var methodCompare = string.Compare(x.Method, y.Method, StringComparison.OrdinalIgnoreCase);
				if (methodCompare != 0)
					return methodCompare;

				if (x.Segments.Length != y.Segments.Length)
					return x.Segments.Length > y.Segments.Length ? 1 : -1;

				for (int i = x.Segments.Length - 1; i >= 0; i--)
				{
					var xi = x.Segments[i]; 
					var yi = y.Segments[i];

					if (xi.IsVar && yi.IsVar)
						continue;

					if (xi.IsVar != yi.IsVar)
						return xi.IsVar ? 1 : -1;

					var nameCompare = string.Compare(xi.Name, yi.Name, StringComparison.OrdinalIgnoreCase);
					if (nameCompare == 0)
						continue;

					return nameCompare;
				}

				return 0;
			}
		}

		internal IAppBuilder App { get; private set; }
		private readonly SortedSet<Route> _routes = new SortedSet<Route>(new RoutePriorityComparer());

		internal MapRouteBuilder(IAppBuilder app)
		{
			if (app == null) throw new ArgumentNullException("app");

			App = app;
		}

		internal MapRouteBuilder Register(string urlTemplate, string method, HandlerFunc handler)
		{
			if (string.IsNullOrWhiteSpace(method)) throw new ArgumentNullException("method");
			if (string.IsNullOrWhiteSpace(urlTemplate)) throw new ArgumentNullException("urlTemplate");
			if (handler == null) throw new ArgumentNullException("handler");

			var segments = RouteBuilderHelper.GetUrlTemplateSegments(urlTemplate);
			var route = new Route
			{
				Method = method,
				Segments = segments,
				Handler = handler
			};
			_routes.Add(route);

			return this;
		}

		internal HandlerFunc GetHandler(IOwinContext ctx)
		{
			foreach (var route in _routes)
			{
				if (!string.Equals(ctx.Request.Method, route.Method, StringComparison.OrdinalIgnoreCase))
					continue;

				var path = ctx.Request.Path.Value.Trim('/');
				var data = RouteBuilderHelper.MatchData(route.Segments, path);
				if (data == null)
					continue;

				ctx.Set(Keys.RouteData, data);
				return route.Handler;
			}

			return null;
		}
	}
}
