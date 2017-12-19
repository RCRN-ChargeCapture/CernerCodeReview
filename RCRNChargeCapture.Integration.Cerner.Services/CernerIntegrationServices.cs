using Hl7.Fhir.Rest;
using RCRNChargeCapture.BusinessServices;
using RCRNChargeCapture.BusinessServices.Interfaces.APIIntegrationInterface;
using RCRNChargeCapture.DataServices.Context;
using RCRNChargeCapture.DataServices.Repositories;
using RCRNChargeCapture.DataServices.UserInfo;
using RCRNChargeCapture.Models;
using RCRNChargeCapture.Models.Cerner;
using RCRNChargeCapture.Models.Master;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Transactions;
using RCRNChargeCapture.ViewModel.APIIntegration.Cerner;

namespace RCRNChargeCapture.Integration.Cerner.Services
{
    public class CernerIntegrationServices : Service<CernerAppointment>, ICernerIntegrationServices
    {
        private IUnitOfWork<IntegrationDB> _uow;
        private ICernerPatientServices _cernerPatientService;
        private ICernerAppointmentServices _cernerAppointmentService;
        private IFHIRConfigurationServices _fhirConfigurationServices;
        public CernerIntegrationServices(ICernerAppointmentServices cernerAppointmentService, ICernerPatientServices cernerPatientService, IFHIRConfigurationServices fhirConfigurationServices, 
            IUnitOfWork<IntegrationDB> uow, IUserInfo userInfo) : base(uow.GetRepository<CernerAppointment>(), userInfo)
        {
            _uow = uow;
            _cernerAppointmentService = cernerAppointmentService;
            _cernerPatientService = cernerPatientService;
            _fhirConfigurationServices = fhirConfigurationServices;
        }

        public bool Sync()
        {
            var accounts = _uow.GetRepository<Account>().GetAll().ToList();
            foreach (var account in accounts)
            {
                var cernerSyncedLocations = _uow.GetRepository<Location>().Queryable().Where(x => x.AccountId == account.AccountId && x.ExternalKey3 != null).ToList();
                foreach (var location in cernerSyncedLocations)
                {
                    CernerResult result = SyncToCarner(location.LocationId);
                    try
                    {
                        using (var scope = new TransactionScope())
                        {
                            foreach (var appointment in result.Appointments)
                            {
                                _cernerAppointmentService.CreateAppointment(appointment);
                            }

                            foreach (var patient in result.Patients)
                            {
                                _cernerPatientService.CreatePatient(patient);
                            }
                            scope.Complete();
                        }
                        _cernerAppointmentService.CreateChargeCaptureAppointments(result.Appointments);
                    }
                    catch (Exception ex)
                    {
                        return false;
                    }
                }
            }
            return true;
        }        
        
        private CernerResult SyncToCarner(long locationId)
        {
            CernerResult result = new CernerResult();

            var token = new CernerLoginServices(_uow, _fhirConfigurationServices).GetToken(locationId);
            if(token != null)
            {
                var location = _uow.GetRepository<Location>().Find(locationId);

                FhirClient fhirClient = new FhirClient(token.dataEndpoint);
                fhirClient.OnBeforeRequest += (object sender, BeforeRequestEventArgs e) =>
                {
                    e.RawRequest.Headers.Add("Authorization", "Bearer " + token.access_token);
                };

                fhirClient.UseFormatParam = true;
                fhirClient.PreferredFormat = ResourceFormat.Json;
                List<string> searchCriteria = new List<string>();
                searchCriteria.Add("location=" + location.ExternalKey3);
                searchCriteria.Add("_count=1000");

                if(token.lastSyncDate !=null)
                {
                    searchCriteria.Add("date=ge" + ((DateTime)token.lastSyncDate).ToString("yyyy-MM-dd"));
                }
                else
                {
                    //searchCriteria.Add("date=ge" + DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd"));
                    searchCriteria.Add("date=gt" + DateTime.Now.AddDays(-1200).ToString("yyyy-MM-dd"));
                }
                searchCriteria.Add("date=le" + DateTime.Now.ToString("yyyy-MM-dd"));

                var query = searchCriteria.ToArray();
                var bundle = fhirClient.Search<Hl7.Fhir.Model.Appointment>(query).Entry.ToList();

                List<CernerAppointment> appointments = new List<CernerAppointment>();
                List<CernerPatient> patients = new List<CernerPatient>();

                foreach (var item in bundle)
                {
                    CernerAppointment appointment = new CernerAppointment();
                    var resource = ((Hl7.Fhir.Model.Appointment)item.Resource);

                    appointment.CernerIntegrationId = resource.Id;
                    appointment.AccountId = _userInfo.AccountId;
                    appointment.AppointmentStatus = (string)((Hl7.Fhir.Model.Appointment)item.Resource).StatusElement.ObjectValue;
                    var appointmentType = ((Hl7.Fhir.Model.Appointment)item.Resource).Type;
                    foreach (var type in appointmentType.Coding)
                    {
                        appointment.AppointmentTypeCode = type.Code;
                        appointment.AppointmentTypeDisplay = type.Display;
                    }
                    appointment.Reason = ((Hl7.Fhir.Model.Appointment)item.Resource).Reason == null ? null : ((Hl7.Fhir.Model.Appointment)item.Resource).Reason.Text;
                    appointment.Description = ((Hl7.Fhir.Model.Appointment)item.Resource).Description;
                    appointment.DurationInMinutes = ((Hl7.Fhir.Model.Appointment)item.Resource).MinutesDuration == null ? 0 : (int)((Hl7.Fhir.Model.Appointment)item.Resource).MinutesDuration;
                    appointment.Comment = ((Hl7.Fhir.Model.Appointment)item.Resource).Comment;
                    DateTimeOffset? dateStart = ((Hl7.Fhir.Model.Appointment)item.Resource).Start;
                    DateTimeOffset? dateEnd = ((Hl7.Fhir.Model.Appointment)item.Resource).End;
                    if (dateStart != null)
                    {
                        appointment.StartDate = ((DateTimeOffset)dateStart).UtcDateTime;
                    }
                    if (dateEnd != null)
                    {
                        appointment.EndDate = ((DateTimeOffset)dateEnd).UtcDateTime;
                    }

                    var participants = ((Hl7.Fhir.Model.Appointment)item.Resource).Participant;
                    foreach (var participant in participants)
                    {
                        if (participant.Actor.Reference != null)
                        {
                            var actor = (participant.Actor.Reference).Split('/');
                            switch (actor[0])
                            {
                                case "Patient":
                                    appointment.PatientId = actor[1];
                                    var existingPatient = patients.Where(x => x.CernerIntegrationId == appointment.PatientId).FirstOrDefault();
                                    if (existingPatient == null)
                                    {
                                        var patient = GetPatientById(appointment.PatientId, token);
                                        patients.Add(patient);
                                    }
                                    break;
                                case "Practitioner":
                                    appointment.PractitionerId = actor[1];
                                    break;
                                case "Location":
                                    appointment.LocationId = actor[1];
                                    break;
                                default:
                                    break;
                            }
                        }
                    }

                    appointments.Add(appointment);
                }
                result.Appointments = appointments;
                result.Patients = patients;

                _fhirConfigurationServices.UpdateLastSyncDate(token.endpointId);
            }
            return result;
        }

        private CernerPatient GetPatientById(string cernerPatientId, Token token)
        {
            CernerPatient result = new CernerPatient();

            FhirClient fhirClient = new FhirClient(token.dataEndpoint);
            fhirClient.OnBeforeRequest += (object sender, BeforeRequestEventArgs e) =>
            {
                e.RawRequest.Headers.Add("Authorization", "Bearer " + token.access_token);
            };

            fhirClient.UseFormatParam = true;
            fhirClient.PreferredFormat = ResourceFormat.Json;
            var patient = fhirClient.Read<Hl7.Fhir.Model.Patient>("Patient/" + cernerPatientId);
            
            if (patient != null)
            {
                result.CernerIntegrationId = patient.Id;
                result.AccountId = _userInfo.AccountId;
                var patientName = patient.Name.FirstOrDefault();
                if (patientName != null)
                {
                    var given = patientName.Given;
                    if (given != null && given.FirstOrDefault() != null)
                    {
                        result.FirstName = given.First();
                        var remainingNames = given.Where(p => p != result.FirstName);
                        if (remainingNames.Count() > 0)
                            result.MiddleName = (string.Join(" ", remainingNames)).Trim();
                    }
                    result.LastName = patientName.Family == null ? null : (string.Join(" ", patientName.Family)).Trim();
                    result.Prefix = patientName.Prefix.FirstOrDefault();
                    result.Suffix = patientName.Suffix.FirstOrDefault();
                }
                DateTime? date = null;
                result.DateOfBirth = patient.BirthDate == null ? date : DateTime.Parse(patient.BirthDate);
                switch (patient.Gender)
                {
                    case Hl7.Fhir.Model.AdministrativeGender.Male:
                        result.Gender = Enums.Gender.Male.ToString();
                        break;
                    case Hl7.Fhir.Model.AdministrativeGender.Female:
                        result.Gender = Enums.Gender.Female.ToString(); ;
                        break;
                    default:
                        result.Gender = Enums.Gender.Unknown.ToString(); ;
                        break;
                }

                var contacts = patient.Telecom;
                foreach (var item in contacts)
                {
                    switch (item.System)
                    {
                        case Hl7.Fhir.Model.ContactPoint.ContactPointSystem.Phone:
                            result.PrimaryPhone = item.Value;
                            break;
                        case Hl7.Fhir.Model.ContactPoint.ContactPointSystem.Fax:
                            result.Fax = item.Value;
                            break;
                        case Hl7.Fhir.Model.ContactPoint.ContactPointSystem.Email:
                            result.Email = item.Value;
                            break;
                        default:
                            break;
                    }

                }

                result.CernerPatientAddress = new List<CernerPatientAddress>();
                List<CernerPatientAddress> addresses = new List<CernerPatientAddress>();

                var address = patient.Address;
                foreach (var item in address)
                {
                    CernerPatientAddress patientAddress = new CernerPatientAddress();
                    patientAddress.Address1 = item.Line.FirstOrDefault();
                    var remainingNames = item.Line.Where(p => p != patientAddress.Address1);
                    if (remainingNames.Count() > 0)
                        patientAddress.Address2 = (string.Join(", ", remainingNames)).Trim();
                    patientAddress.CityName = item.City;
                    patientAddress.StateAbbr = item.State;
                    patientAddress.ZipCode = item.PostalCode;

                    addresses.Add(patientAddress);
                }

                if (addresses.Count > 0)
                {
                    result.CernerPatientAddress = addresses;
                }
            }

            return result;
        }

        public List<CernerLocationViewModel> GetCernerLocations()
        {
            var locations = _uow.GetRepository<Location>().Queryable().Where(x => x.ExternalKey3 != null && x.AccountId == _userInfo.AccountId)
                .Select(x => new CernerLocationViewModel() { LocationId = x.LocationId, LocationName = x.Name, CernerLocationId = x.ExternalKey3 });
            if(locations != null)
            {
                return locations.ToList();
            }
            return null;

        }
    }

    internal class CernerResult
    {
        public List<CernerAppointment> Appointments { get; set; }
        public List<CernerPatient> Patients { get; set; }

        public CernerResult()
        {
            Appointments = new List<CernerAppointment>();
            Patients = new List<CernerPatient>();
        }
    }
}
