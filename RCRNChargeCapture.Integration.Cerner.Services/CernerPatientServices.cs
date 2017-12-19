using RCRNChargeCapture.BusinessServices;
using RCRNChargeCapture.BusinessServices.Interfaces.ChargeInterface;
using RCRNChargeCapture.DataServices.Context;
using RCRNChargeCapture.DataServices.Repositories;
using RCRNChargeCapture.DataServices.UserInfo;
using RCRNChargeCapture.Models.Cerner;
using RCRNChargeCapture.Models.Charge;
using RCRNChargeCapture.Models.Master;
using RCRNChargeCapture.Models.Security;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Transactions;
using static RCRNChargeCapture.Models.Enums;
using RCRNChargeCapture.ViewModel.APIIntegration.Cerner;

namespace RCRNChargeCapture.Integration.Cerner.Services
{
    public class CernerPatientServices : Service<CernerPatient>, ICernerPatientServices
    {
        private IUnitOfWork<ChargeCaptureDB> _uow;
        private readonly IPatientServices _patientServices;
        private readonly IAssignmentServices _assignmentServices;
        private readonly IAdmissionServices _admissionServices;
        private readonly ICernerAppointmentServices _cernerAppointmentService;
        public CernerPatientServices(IPatientServices patientServices, IAssignmentServices assignmentServices, IAdmissionServices admissionServices,
            ICernerAppointmentServices cernerAppointmentService, IUnitOfWork<ChargeCaptureDB> uow, IUserInfo userInfo) : base(uow.GetRepository<CernerPatient>(), userInfo)
        {
            _uow = uow;
            _patientServices = patientServices;
            _assignmentServices = assignmentServices;
            _admissionServices = admissionServices;
            _cernerAppointmentService = cernerAppointmentService;
        }

        public long CreateCernerPatientToChargeCapture(long cernerPatientId, string cernerLocationId)
        {
            CernerPatientCreationInput input = new CernerPatientCreationInput();
            List<CernerAdmission> admissions = new List<CernerAdmission>();
                        
            var cernerPatient = _repository.Find(cernerPatientId);

            var existingPatient = _uow.GetRepository<Patient>().Queryable().Where(x => x.ExternalKey3 == cernerPatient.CernerIntegrationId).FirstOrDefault();
            if(existingPatient == null)
            {
                var cernerPatientAddress = _uow.GetRepository<CernerPatientAddress>().Queryable().Where(x => x.CernerPatientId == cernerPatientId);
                Enum.TryParse(cernerPatient.Gender, out Gender patientGender);
                input.Patient = new Patient()
                {
                    DateOfBirth = cernerPatient.DateOfBirth,
                    FirstName = cernerPatient.FirstName,
                    LastName = cernerPatient.LastName,
                    Gender = patientGender,
                    MiddleName = cernerPatient.MiddleName,
                    Prefix = cernerPatient.Prefix,
                    Suffix = cernerPatient.Suffix,
                    ExternalKey3 = cernerPatient.CernerIntegrationId,                    
                    AccountId = _userInfo.AccountId
                };
                if(cernerPatient.PrimaryPhone != null || cernerPatient.Email != null || cernerPatient.Fax != null)
                {
                    input.Patient.Contact = new Contact()
                    {
                        Email = cernerPatient.Email,
                        Fax = cernerPatient.Fax,
                        PrimaryPhone = cernerPatient.PrimaryPhone
                    };
                    input.Patient.ContactId = input.Patient.Contact.ContactId;
                }
                if(cernerPatientAddress != null && cernerPatientAddress.Count() > 0)
                {
                    var singleAddress = cernerPatientAddress.FirstOrDefault();
                    int? stateId = null;
                    var state = _uow.GetRepository<State>().Queryable().Where(x => x.Abbr == singleAddress.StateAbbr).FirstOrDefault();
                    if (state != null)
                    {
                        stateId = state.StateId;
                    }
                    input.Patient.Address = new Address()
                    {
                        Address1 = singleAddress.Address1,
                        Address2 = singleAddress.Address2,
                        CityName = singleAddress.CityName,
                        StateId = stateId,
                        ZipCode = singleAddress.ZipCode
                    };
                    input.Patient.AddressId = input.Patient.Address.AddressId;
                }                
            }
            else
            {
                input.Patient = existingPatient;
            }
            
            var appointments = _uow.GetRepository<CernerAppointment>().Queryable().Where(x => x.PatientId == cernerPatient.CernerIntegrationId && x.IsIntegrated == false && x.LocationId == cernerLocationId).ToList();
            var appointmentsGroupedByLocation = appointments.GroupBy(u => u.LocationId)
                   .Select(grp => new {
                       LocationId = grp.Key,
                       Appointments = grp
                   }).ToList();            
            foreach (var appointment in appointmentsGroupedByLocation)
            {
                CernerAdmission newAdmission = new CernerAdmission();
                var location = _uow.GetRepository<Location>().Queryable().Where(x => x.ExternalKey3 == appointment.LocationId).FirstOrDefault();
                Admission existingAdmission = null;
                if(existingPatient!=null)
                {
                    existingAdmission = _uow.GetRepository<Admission>().Queryable().Where(x => x.PatientId == existingPatient.PatientId && x.DischargeDate == null && x.LocationId == location.LocationId).FirstOrDefault();
                }

                if(existingAdmission == null)
                {
                    var latestAppointment = appointment.Appointments.OrderByDescending(x => x.StartDate).FirstOrDefault();
                    newAdmission.Admission = new Admission
                    {
                        AdmissionDate = ((DateTime)latestAppointment.StartDate).ToUniversalTime(),
                        LocationId = location.LocationId
                    };
                }
                else
                {
                    newAdmission.Admission = existingAdmission;
                }

                var appointmentsGroupedByDoctor = appointment.Appointments.GroupBy(u => u.PractitionerId)
                   .Select(grp => new {
                       PractitionerId = grp.Key,
                       Appointments = grp
                   }).ToList();

                foreach (var item in appointmentsGroupedByDoctor)
                {
                    var doctor = _uow.GetRepository<Doctor>().Queryable().Where(x => x.ExternalKey3 == item.PractitionerId).FirstOrDefault();
                    var assistant = _uow.GetRepository<Assistant>().Queryable().Where(x => x.ExternalKey3 == item.PractitionerId).FirstOrDefault();
                    if (doctor != null || assistant != null)
                    {
                        Assignment existingAssignment = null;
                        if (existingAdmission != null)
                        {
                            existingAssignment = _uow.GetRepository<Assignment>().Queryable().Where(x => x.AdmissionId == existingAdmission.AdmissionId && x.DoctorId == doctor.DoctorId && x.EndDate == null).FirstOrDefault();
                        }
                        if (existingAssignment == null)
                        {
                            var latestAssignment = item.Appointments.OrderByDescending(x => x.StartDate).FirstOrDefault();
                            var newAssignment = new Assignment
                            {
                                StartDate = ((DateTime)latestAssignment.StartDate).ToUniversalTime(),
                                AdmissionId = (existingAdmission != null ? existingAdmission.AdmissionId : 0),
                                CreatedBy = _userInfo.UserId,
                            };
                            if (doctor != null)
                            {
                                newAssignment.DoctorId = doctor.DoctorId;
                            }
                            if (assistant != null)
                            {
                                var applicationUser = _uow.GetRepository<ApplicationUser>().Queryable().Where(x => x.AssistantId == assistant.AssistantId && x.Type == UserType.Assistant).FirstOrDefault();
                                newAssignment.DoctorId = assistant.AssistantId;
                                newAssignment.SupervisorId = applicationUser.DoctorId;
                            }
                            if(newAdmission.Admission.AdmissionId == 0)
                            {
                                newAdmission.Admission.AdmissionDate = ((DateTime)latestAssignment.StartDate).ToUniversalTime();
                            }
                            newAdmission.Assignments.Add(newAssignment);
                        }
                    }
                }
                input.Admissions.Add(newAdmission);
            }

            using (var scope = new TransactionScope())
            {
                if(input.Patient.PatientId == 0)
                {
                    _patientServices.CreatePatient(input.Patient);
                }
                foreach (var admission in input.Admissions)
                {
                    if(admission.Admission.AdmissionId == 0)
                    {
                        admission.Admission.PatientId = input.Patient.PatientId;
                        _admissionServices.CreateAdmission(admission.Admission);
                    }

                    foreach (var assignment in admission.Assignments)
                    {
                        assignment.AdmissionId = admission.Admission.AdmissionId;
                        _assignmentServices.Create(assignment);
                    }

                }
                cernerPatient.IsIntegrated = true;
                UpdatePatient(cernerPatient);
                _cernerAppointmentService.CreatePatientAppointments(cernerPatient.CernerIntegrationId);
                scope.Complete();

                return input.Patient.PatientId;
            }

        }

        public long CreatePatient(CernerPatient patientEntity)
        {
            var existingPatient = GetPatientByCernerId(patientEntity.CernerIntegrationId);
            if (existingPatient == null)
            {
                CernerPatient patient = new CernerPatient()
                {
                    CernerIntegrationId = patientEntity.CernerIntegrationId,
                    DateOfBirth = patientEntity.DateOfBirth,
                    Email = patientEntity.Email,
                    Fax = patientEntity.Fax,
                    FirstName = patientEntity.FirstName,
                    Gender = patientEntity.Gender,
                    IsIntegrated = false,
                    LastName = patientEntity.LastName,
                    MiddleName = patientEntity.MiddleName,
                    Prefix = patientEntity.Prefix,
                    PrimaryPhone = patientEntity.PrimaryPhone,
                    Suffix = patientEntity.Suffix,
                    SyncDate = DateTime.Now.ToUniversalTime(),
                    AccountId = patientEntity.AccountId
                };

                _repository.Add(patient);
                _uow.SaveChanges();
                foreach (var item in patientEntity.CernerPatientAddress)
                {
                    item.CernerPatientId = patient.CernerPatientId;
                    _uow.GetRepository<CernerPatientAddress>().Add(item);
                }
                _uow.SaveChanges();

                return patient.CernerPatientId;
            }
            return 0;
        }

        public List<CernerPatient> CreatePatient(List<CernerPatient> patientList)
        {
            foreach (var item in patientList)
            {
                var result = CreatePatient(item);
                item.CernerPatientId = result;
            }
            return patientList;
        }

        public bool DeletePatient(long patientId)
        {
            var success = false;
            if (patientId > 0)
            {
                using (var scope = new TransactionScope())
                {
                    var patient = _repository.Find(patientId);
                    if (patient != null)
                    {
                        _repository.Delete(patient);
                        _uow.SaveChanges();
                        scope.Complete();
                        success = true;
                    }
                }
            }
            return success;
        }

        public CernerPatientSearchResult GetCernerPatients(string cernerLocationId)
        {
            Dictionary<string, ApplicationUser> doctorHash = new Dictionary<string, ApplicationUser>();
            CernerPatientSearchResult result = new CernerPatientSearchResult();
            var patientIds = _uow.GetRepository<CernerAppointment>().Queryable()
                .Where(x => x.LocationId == cernerLocationId && x.AccountId == _userInfo.AccountId)
                .Select(x => new { PatientId = x.PatientId, PractitionerId = x.PractitionerId }).Distinct().ToList();

            foreach (var item in patientIds)
            {
                CernerPatientViewModel patient = new CernerPatientViewModel();
                var cernerPatient = _repository.Queryable().Where(x => x.CernerIntegrationId == item.PatientId && x.IsIntegrated == false && x.AccountId == _userInfo.AccountId).FirstOrDefault();
                if (cernerPatient != null)
                {
                    patient.CernerIntegrationId = cernerPatient.CernerIntegrationId;
                    patient.CernerPatientId = cernerPatient.CernerPatientId;
                    patient.FirstName = cernerPatient.FirstName;
                    patient.LastName = cernerPatient.LastName;
                    patient.CernerLocationId = cernerLocationId;

                    ApplicationUser doctorName = null;

                    if (item.PractitionerId != null && !doctorHash.TryGetValue(item.PractitionerId, out doctorName))
                    {
                        long createdById = -1;
                        UserType createdbyType = UserType.Guest;
                        Assistant assistant = new Assistant();
                        var doctor = _uow.GetRepository<Doctor>().Queryable().Where(x => x.ExternalKey3 == item.PractitionerId).FirstOrDefault();
                        if (doctor == null)
                        {
                            assistant = _uow.GetRepository<Assistant>().Queryable().Where(x => x.ExternalKey3 == item.PractitionerId).FirstOrDefault();
                            if (assistant != null)
                            {
                                createdById = assistant.AssistantId;
                                createdbyType = UserType.Assistant;
                            }
                        }
                        else
                        {
                            createdById = doctor.DoctorId;
                            createdbyType = UserType.Doctor;
                        }

                        if (createdById > 0)
                        {
                            switch (createdbyType)
                            {
                                case UserType.Doctor:
                                    var createdBy = _uow.GetRepository<ApplicationUser>().Queryable().Where(u => u.DoctorId == createdById && u.Type == UserType.Doctor && u.AccountId == _userInfo.AccountId).FirstOrDefault();
                                    doctorHash.Add(item.PractitionerId, createdBy);
                                    doctorName = createdBy;
                                    break;
                                case UserType.Assistant:
                                    var createdByAs = _uow.GetRepository<ApplicationUser>().Queryable().Where(u => u.AssistantId == createdById && u.Type == UserType.Assistant && u.AccountId == _userInfo.AccountId).FirstOrDefault();
                                    doctorHash.Add(item.PractitionerId, createdByAs);
                                    doctorName = createdByAs;
                                    break;
                                default:
                                    break;
                            }
                        }
                    }
                    if (doctorName != null && doctorName.AccountId != string.Empty)
                    {
                        patient.CernerPractitionerId = item.PractitionerId;
                        patient.DoctorName = doctorName.LastName + ", " + doctorName.FirstName;
                        result.Patients.Add(patient);
                    }
                    else
                    {
                        result.Conflicts.Add(patient);
                    }
                }
            }

            return result;
        }

        public CernerPatient GetPatientByCernerId(string cernerPatientId)
        {
            var patient = _repository.Queryable().Where(x => x.CernerIntegrationId == cernerPatientId).FirstOrDefault();
            if (patient != null)
            {
                return patient;
            }
            return null;
        }

        public CernerPatient GetPatientById(long patientId)
        {
            var patient = _repository.Find(patientId);
            if (patient != null)
            {
                return patient;
            }
            return null;
        }

        public List<Patient> GetUnMappedPatients()
        {
            var patients = _uow.GetRepository<Patient>().Queryable().Where(x => x.ExternalKey3 == null && x.AccountId == _userInfo.AccountId).ToList();
            return patients;
        }

        public bool SyncPatients(List<CernerPatientViewModel> patientList)
        {
            var newPatients = patientList.Where(x => x.PatientIdToBeMapped == null).ToList();
            var patientsToBeMapped = patientList.Where(x => x.PatientIdToBeMapped != null).ToList();
            using (var scope = new TransactionScope())
            {
                foreach (var item in newPatients)
                {
                    CreateCernerPatientToChargeCapture(item.CernerPatientId, item.CernerLocationId);
                }

                foreach (var item in patientsToBeMapped)
                {
                    var patient = _patientServices.GetPatientById((long)item.PatientIdToBeMapped);
                    patient.ExternalKey3 = item.CernerIntegrationId;
                    if (item.IsReordToBeUpdated)
                    {
                        patient.FirstName = item.FirstName;
                        patient.LastName = item.LastName;
                    }
                    _uow.GetRepository<Patient>().Update(patient);
                    _uow.SaveChanges();
                    CreateCernerPatientToChargeCapture(item.CernerPatientId, item.CernerLocationId);
                }
                scope.Complete();
            }

            return true;
        }

        public bool UpdatePatient(CernerPatient patientEntity)
        {
            var success = false;
            if (patientEntity != null)
            {
                _repository.Update(patientEntity);
                _uow.SaveChanges();
                success = true;

            }
            return success;
        }
    }

    internal class CernerAdmission
    {
        public Admission Admission { get; set; }
        public List<Assignment> Assignments { get; set; }

        public CernerAdmission()
        {
            Admission = new Admission();
            Assignments = new List<Assignment>();
        }
    }

    internal class CernerPatientCreationInput
    {
        public Patient Patient { get; set; }
        public List<CernerAdmission> Admissions { get; set; }

        public CernerPatientCreationInput()
        {
            Patient = new Patient();
            Admissions = new List<CernerAdmission>();
        }
    }
}
