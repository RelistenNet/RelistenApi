
using System;
using Hangfire.Server;
using Hangfire.Console;
using System.Diagnostics;
using System.Text;
using System.Collections;
using Newtonsoft.Json;

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

            ctx.WriteLine(e.ToString());
            ctx.WriteLine("> Exception Data: ");
            
            foreach (DictionaryEntry kvp in e.Data) {
                var val = "";
                try {
                    val = JsonConvert.SerializeObject(kvp.Value, new JsonSerializerSettings 
                    {
                        PreserveReferencesHandling = PreserveReferencesHandling.Objects
                    });
                }
                catch(JsonSerializationException e)
                {
                    val = kvp.Value.ToString();
                }

                ctx.WriteLine($"\t{kvp.Key}: {val}");
            }
        }
    }
}
