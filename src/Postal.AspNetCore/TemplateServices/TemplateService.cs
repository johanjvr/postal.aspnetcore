using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Postal.AspNetCore
{
    public class TemplateService : ITemplateService
    {
        public static readonly string ViewExtension = ".cshtml";

        private readonly ILogger<TemplateService> _logger;
        private readonly IRazorViewEngine _viewEngine;
        private readonly IServiceProvider _serviceProvider;
        private readonly ITempDataProvider _tempDataProvider;
#if NETSTANDARD2_0
        private readonly Microsoft.AspNetCore.Hosting.IHostingEnvironment _hostingEnvironment;
#else
        private readonly Microsoft.Extensions.Hosting.IHostEnvironment _hostingEnvironment;
#endif

#if NETSTANDARD2_0
        public TemplateService(
            ILogger<TemplateService> logger,
            IRazorViewEngine viewEngine,
            IServiceProvider serviceProvider,
            ITempDataProvider tempDataProvider,
            Microsoft.AspNetCore.Hosting.IHostingEnvironment hostingEnvironment
            )
#else
        public TemplateService(
            ILogger<TemplateService> logger,
            IRazorViewEngine viewEngine,
            IServiceProvider serviceProvider,
            ITempDataProvider tempDataProvider,
            Microsoft.Extensions.Hosting.IHostEnvironment hostingEnvironment
            )
#endif
        {
            _logger = logger;
            _viewEngine = viewEngine;
            _serviceProvider = serviceProvider;
            _tempDataProvider = tempDataProvider;
            _hostingEnvironment = hostingEnvironment;
        }

        public async Task<string> RenderTemplateAsync<TViewModel>(RouteData routeData,
            string viewName, TViewModel viewModel, Dictionary<string, object> additonalViewDictionary = null, bool isMainPage = true) where TViewModel : IViewData
        {
            var httpContext = new DefaultHttpContext
            {
                RequestServices = _serviceProvider
            };
            if (viewModel.RequestPath != null)
            {
                httpContext.Request.Host = HostString.FromUriComponent(viewModel.RequestPath.Host);
                httpContext.Request.Scheme = viewModel.RequestPath.Scheme;
                httpContext.Request.PathBase = PathString.FromUriComponent(viewModel.RequestPath.PathBase);

                _logger.LogDebug($"RequestPath != null");
                _logger.LogTrace($"\tHost: {viewModel.RequestPath.Host} -> {httpContext.Request.Host}");
                _logger.LogTrace($"\tScheme: {viewModel.RequestPath.Scheme}");
                _logger.LogTrace($"\tPathBase: {viewModel.RequestPath.PathBase} -> {httpContext.Request.PathBase}");
            }

            var actionDescriptor = new ActionDescriptor
            {
                RouteValues = routeData.Values.ToDictionary(kv => kv.Key, kv => kv.Value.ToString())
            };
            var actionContext = new ActionContext(httpContext, routeData, actionDescriptor);

            using (var outputWriter = new StringWriter())
            {
                Microsoft.AspNetCore.Mvc.ViewEngines.ViewEngineResult viewResult = null;
                if (IsApplicationRelativePath(viewName) || IsRelativePath(viewName))
                {
                    _logger.LogDebug($"Relative path");
#if NETSTANDARD2_0  
                    viewResult = _viewEngine.GetView(_hostingEnvironment.WebRootPath, viewName, isMainPage);
#else
                    if (_hostingEnvironment is Microsoft.AspNetCore.Hosting.IWebHostEnvironment webHostEnvironment)
                    {
                        _logger.LogDebug($"_hostingEnvironment is IWebHostEnvironment -> GetView from WebRootPath: {webHostEnvironment.WebRootPath}");
                        viewResult = _viewEngine.GetView(webHostEnvironment.WebRootPath, viewName, isMainPage);
                    }
                    else
                    {
                        _logger.LogDebug($"_hostingEnvironment is IHostEnvironment -> GetView from ContentRootPath: {_hostingEnvironment.ContentRootPath}");
                        viewResult = _viewEngine.GetView(_hostingEnvironment.ContentRootPath, viewName, isMainPage);
                    }
#endif
                }
                else
                {
                    _logger.LogDebug($"Not a relative path");
                    viewResult = _viewEngine.FindView(actionContext, viewName, isMainPage);
                }


                var viewDictionary = new ViewDataDictionary<TViewModel>(viewModel.ViewData, viewModel);
                if (additonalViewDictionary != null)
                {
                    _logger.LogDebug($"additonalViewDictionary count: {additonalViewDictionary.Count}");
                    foreach (var kv in additonalViewDictionary)
                    {
                        if (!viewDictionary.ContainsKey(kv.Key))
                        {
                            viewDictionary.Add(kv);
                        }
                        else
                        {
                            viewDictionary[kv.Key] = kv.Value;
                        }
                    }
                }

                var tempDataDictionary = new TempDataDictionary(httpContext, _tempDataProvider);

                if (!viewResult.Success)
                {
                    _logger.LogError($"Failed to render template {viewName} because it was not found. \r\nThe following locations are searched: \r\n{string.Join("\r\n", viewResult.SearchedLocations) }");
                    throw new TemplateServiceException($"Failed to render template {viewName} because it was not found. \r\nThe following locations are searched: \r\n{string.Join("\r\n", viewResult.SearchedLocations) }");
                }

                try
                {
                    var viewContext = new ViewContext(actionContext, viewResult.View, viewDictionary,
                        tempDataDictionary, outputWriter, new HtmlHelperOptions());

                    await viewResult.View.RenderAsync(viewContext);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to render template due to a razor engine failure");
                    throw new TemplateServiceException("Failed to render template due to a razor engine failure", ex);
                }

                await outputWriter.FlushAsync();
                return outputWriter.ToString();
            }
        }

        // https://github.com/aspnet/AspNetCore/blob/c565386a3ed135560bc2e9017aa54a950b4e35dd/src/Mvc/Mvc.Razor/src/RazorViewEngine.cs#L478
        private static bool IsApplicationRelativePath(string name)
        {
            Debug.Assert(!string.IsNullOrEmpty(name));
            return name[0] == '~' || name[0] == '/';
        }

        private static bool IsRelativePath(string name)
        {
            Debug.Assert(!string.IsNullOrEmpty(name));

            // Though ./ViewName looks like a relative path, framework searches for that view using view locations.
            return name.EndsWith(ViewExtension, StringComparison.OrdinalIgnoreCase);
        }
    }
}