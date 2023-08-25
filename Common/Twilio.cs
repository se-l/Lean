using System;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using QuantConnect.Configuration;
using Twilio.Types;


namespace QuantConnect
{
    /// <summary>
    /// TwilioClient to make emergency calls
    /// </summary>
    public static class Twilio
    {
        /// <summary>
        /// Call your configured phone numer
        /// </summary>
        public static void Call()
        {
            // Find your Account SID and Auth Token at twilio.com/console
            // and set the environment variables. See http://twil.io/secure
            TwilioClient.Init(Config.Get("TwilioAccountSid"), Config.Get("TwilioAuthToken"));
            PhoneNumber phoneNumber = new(Config.Get("TwilioPhoneNumber"));

            var call = CallResource.Create(
                url: new Uri("http://demo.twilio.com/docs/voice.xml"),
                to: phoneNumber,
                from: phoneNumber
            );

            Console.Write(call.Sid);
        }
    }

}
