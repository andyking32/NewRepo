// --------------------------------------------------------------------------------------------------------------------
// <copyright file="GlobalExceptionHandler.cs" company="BIS">BIS</copyright>
// <summary>Defines the GlobalExceptionHandler type.</summary>
// --------------------------------------------------------------------------------------------------------------------

namespace ResourceMgmt.Api.ErrorHandling
{
    using System;
    using System.Configuration;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Security;
    using System.Web;
    using System.Web.Http.ExceptionHandling;

    using Bis.Common.Conversion;
    using Bis.Common.Diagnostics;
    using Bis.Common.ErrorHandling;

    /// <summary>The global exception handler.</summary>
    public class GlobalExceptionHandler : ExceptionHandler
    {
        /// <summary>The _show exception details in response.</summary>
        private static readonly bool ShowExceptionDetailsInResponse;

        /// <summary>Initialises static members of the <see cref="GlobalExceptionHandler"/> class.</summary>
        static GlobalExceptionHandler()
        {
            // configuration service might not exist because of unhadled exception that's why we use ConfigurationManager directly
            ShowExceptionDetailsInResponse = ConfigurationManager.AppSettings["ShowExceptionDetailsInResponse"].ParseToBool();
        }

        /// <summary>The handler.</summary>
        /// <param name="context">The context.</param>
        public override void Handle(ExceptionHandlerContext context)
        {
            var logIdentifier = LogException(context);
            
            HttpStatusCode statusCode;

            var message = ShowExceptionDetailsInResponse    ? string.Format("{0} - {1}", logIdentifier, context.Exception.ToTraceString())
                                                            : logIdentifier.ToString();
            
            if (context.Exception is BusinessException || context.Exception.GetBaseException() is BusinessException)
            {
                statusCode = (HttpStatusCode)444;
            }
            else if (context.Exception is SecurityException)
            {
                statusCode = HttpStatusCode.Forbidden;
            }
            else
            {
                statusCode = HttpStatusCode.InternalServerError;
            }

            context.Result = new ExceptionResponse
            {
                StatusCode = statusCode,
                Message = message,
                Request = context.Request
            };
        }

        /// <summary>Override of should handle method.</summary>
        /// <param name="context">The context.</param>
        /// <returns>The <see cref="bool"/>.</returns>
        public override bool ShouldHandle(ExceptionHandlerContext context)
        {
            return true;
        }

        /// <summary>Log the exception.</summary>
        /// <param name="context">The context.</param>
        /// <returns>The <see cref="Guid"/>.</returns>
        private static Guid LogException(ExceptionHandlerContext context)
        {
            string bodyContent = null;
            var logIdentifier = Guid.NewGuid();

            var log = log4net.LogManager.GetLogger(context.ExceptionContext.ControllerContext.Controller.ToString());

            // If the request method is post or put then we need to get the request content from the body in order to recreate the error
            if (context.Request.Method == HttpMethod.Post || context.Request.Method == HttpMethod.Put)
            {
                bodyContent = GetBodyContent();
            }

            log4net.LogicalThreadContext.Properties["applicationLogIdentifier"] = logIdentifier;
            log4net.LogicalThreadContext.Properties["createdBy"] = HttpContext.Current.User.Identity.Name;
            log4net.LogicalThreadContext.Properties["httpMethod"] = context.Request.Method;
            log4net.LogicalThreadContext.Properties["requestUrl"] = context.Request.RequestUri.ToString();
            log4net.LogicalThreadContext.Properties["requestBodyContent"] = bodyContent;
            
            if (context.Exception is BusinessException)
            {
                log.Error(context.Exception.Message, context.Exception);
            }
            else
            {
                log.Fatal(context.Exception.Message, context.Exception);
            }

            return logIdentifier;
        }

        /// <summary>Read the body content.</summary>
        /// <returns>The <see cref="string"/>.</returns>
        private static string GetBodyContent()
        {
            string content;

            using (var streamReader = new StreamReader(HttpContext.Current.Request.InputStream))
            { 
                HttpContext.Current.Request.InputStream.Position = 0;
                
                content = streamReader.ReadToEnd();
            }

            return content;
        }
    }
}