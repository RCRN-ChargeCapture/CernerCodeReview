using Newtonsoft.Json;
using RCRNChargeCapture.BusinessServices.Interfaces.APIIntegrationInterface;
using RCRNChargeCapture.DataServices.Context;
using RCRNChargeCapture.DataServices.Repositories;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using static RCRNChargeCapture.Models.Enums;

namespace RCRNChargeCapture.Integration.Cerner.Services
{
    public class CernerLoginServices : ICernerService
    {
        static IUnitOfWork<IntegrationDB> _uow = null;
        IFHIRConfigurationServices _fhirConfigService;
        public CernerLoginServices(IUnitOfWork<IntegrationDB> uow, IFHIRConfigurationServices fhirConfigService)
        {
            _uow = uow;
            _fhirConfigService = fhirConfigService;
        }

        public Token GetToken(long locationId)
        {
            var configuration = _fhirConfigService.GetFHIRApplicationConfiguration(FHIRProvider.Cerner);
            var endpoint = _fhirConfigService.GetFHIREndpointConfiguration(configuration.FHIRApplicationConfigurationId, locationId);

            byte[] bytes = Encoding.UTF8.GetBytes(configuration.ClientId + ":" + configuration.SecretKey);
            string base64String = Convert.ToBase64String(bytes);

            var pairs = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>( "grant_type", "client_credentials" ),
                new KeyValuePair<string, string>( "scope", "system/Appointment.read,system/Patient.read")
            };
            var content = new FormUrlEncodedContent(pairs);
            ServicePointManager.ServerCertificateValidationCallback += (sender, cert, chain, sslPolicyErrors) => true;
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.Add("Authorization", "Basic " + base64String);
                var response = client.PostAsync(endpoint.TokenURL, content).Result;
                if (response.IsSuccessStatusCode)
                {
                    var token = JsonConvert.DeserializeObject<Token>(response.Content.ReadAsStringAsync().Result);
                    token.providerConfigId = configuration.FHIRApplicationConfigurationId;
                    token.endpointId = endpoint.FHIREndpointConfigurationId;
                    token.dataEndpoint = endpoint.DataEndpoint;
                    token.lastSyncDate = endpoint.LastSyncDate;
                    return token;
                }
                else
                {
                    return null;
                }
            }
        }
    }

    public class Token
    {
        public string access_token { get; set; }
        public int expires_in { get; set; }
        public string scope { get; set; }
        public long providerConfigId { get; set; }
        public long endpointId { get; set; }
        public string dataEndpoint { get; set; }
        public DateTime? lastSyncDate { get; set; }
    }
}
