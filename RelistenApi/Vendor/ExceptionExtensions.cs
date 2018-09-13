
using System;
using Hangfire.Server;
using Hangfire.Console;
using System.Diagnostics;
using System.Text;

namespace Relisten.Import
{
    public static class ExceptionExtensions
    {
        public static void LogException(this PerformContext ctx, Exception e)
        {
            if (ctx == null)
            {
                return;
            }

            var st = new StackTrace(e, true);
            var frames = st.GetFrames();
            var traceString = new StringBuilder();

            foreach (var frame in frames)
            {
                if (frame.GetFileLineNumber() < 1)
                    continue;

                traceString.Append("File: " + frame.GetFileName());
                traceString.Append(", Method:" + frame.GetMethod().Name);
                traceString.Append(", LineNumber: " + frame.GetFileLineNumber());
                traceString.Append("  -->  ");
            }

            ctx.WriteLine(traceString.ToString());
            ctx.WriteLine(e.ToString());
        }
    }
}
