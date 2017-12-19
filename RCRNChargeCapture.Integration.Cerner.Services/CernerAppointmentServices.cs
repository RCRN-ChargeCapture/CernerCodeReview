using RCRNChargeCapture.BusinessServices;
using RCRNChargeCapture.BusinessServices.Interfaces.AppointmentInterface;
using RCRNChargeCapture.DataServices.Context;
using RCRNChargeCapture.DataServices.Repositories;
using RCRNChargeCapture.DataServices.UserInfo;
using RCRNChargeCapture.Models.Appointments;
using RCRNChargeCapture.Models.Cerner;
using RCRNChargeCapture.Models.Charge;
using RCRNChargeCapture.Models.Master;
using System;
using System.Collections.Generic;
using System.Linq;
using static RCRNChargeCapture.Models.Enums;

namespace RCRNChargeCapture.Integration.Cerner.Services
{
    public class CernerAppointmentServices : Service<CernerAppointment>, ICernerAppointmentServices
    {
        private IUnitOfWork<ChargeCaptureDB> _uow;
        private IAppointmentServices _appointmentServices;
        public CernerAppointmentServices(IAppointmentServices appointmentServices, IUnitOfWork<ChargeCaptureDB> uow, IUserInfo userInfo) : base(uow.GetRepository<CernerAppointment>(), userInfo)
        {
            _uow = uow;
            _appointmentServices = appointmentServices;
        }

        public long CreateAppointment(CernerAppointment appointmentEntity)
        {
            var existingPatient = GetAppointmentByCernerId(appointmentEntity.CernerIntegrationId);
            if (existingPatient == null)
            {
                appointmentEntity.IsIntegrated = false;
                appointmentEntity.SyncDate = DateTime.Now.ToUniversalTime();
                _repository.Add(appointmentEntity);
                _uow.SaveChanges();
                return appointmentEntity.CernerAppointmentId;
            }
            return 0;
        }

        public List<CernerAppointment> CreateAppointment(List<CernerAppointment> appointmentList)
        {
            foreach (var item in appointmentList)
            {
                var result = CreateAppointment(item);
                item.CernerAppointmentId = result;
            }
            return appointmentList;
        }

        public CernerAppointment GetAppointmentByCernerId(string cernerAppointmentId)
        {
            var appointment = _repository.Queryable().Where(x => x.CernerIntegrationId == cernerAppointmentId).FirstOrDefault();
            if (appointment != null)
            {
                return appointment;
            }
            return null;
        }

        public bool UpdateAppointment(CernerAppointment appointmentEntity)
        {
            if(appointmentEntity != null)
            {
                appointmentEntity.IsIntegrated = true;
                _repository.Update(appointmentEntity);
                _uow.SaveChanges();
                return true;
            }
            return false;
        }

        public bool CreateChargeCaptureAppointments(List<CernerAppointment> appointments)
        {
            var patientRepository = _uow.GetRepository<Patient>();
            List<CernerAppointment> updatedAppointments = new List<CernerAppointment>();
            var doctorRepository = _uow.GetRepository<Doctor>();
            var locationRepository = _uow.GetRepository<Location>();
            List<Appointment> newAppointments = new List<Appointment>();
            foreach (var item in appointments)
            {
                if (item.AppointmentStatus != "cancelled" && item.PatientId != null && item.PractitionerId != null && item.LocationId != null)
                {
                    var patient = patientRepository.Queryable().Where(x => x.ExternalKey3 == item.PatientId).FirstOrDefault();
                    if (patient != null)
                    {
                        var doctor = doctorRepository.Queryable().Where(x => x.ExternalKey3 == item.PractitionerId).FirstOrDefault();
                        if (doctor != null)
                        {
                            var location = locationRepository.Queryable().Where(x => x.ExternalKey3 == item.LocationId).FirstOrDefault();
                            Appointment appointment = new Appointment();
                            appointment.Participants = new List<Participant>();

                            appointment.AccountId = patient.AccountId;
                            appointment.Comments = item.Comment;
                            appointment.Description = item.Description;
                            appointment.Duration = item.DurationInMinutes;
                            appointment.EndingTime = item.EndDate;
                            appointment.ExternalKey3 = item.CernerIntegrationId;
                            appointment.IsCancelled = false;
                            appointment.Reason = (item.Reason != null ? item.Reason : "Integrated from cerner");
                            appointment.StartingTime = item.StartDate;

                            switch (item.AppointmentStatus)
                            {
                                case "proposed":
                                    appointment.Status = AppointmentStatus.Proposed;
                                    break;
                                case "pending":
                                    appointment.Status = AppointmentStatus.Pending;
                                    break;
                                case "booked":
                                    appointment.Status = AppointmentStatus.Booked;
                                    break;
                                case "arrived":
                                    appointment.Status = AppointmentStatus.Arrived;
                                    break;
                                case "fulfilled":
                                    appointment.Status = AppointmentStatus.Fulfilled;
                                    break;
                                case "cancelled":
                                    appointment.Status = AppointmentStatus.Cancelled;
                                    break;
                                case "noshow":
                                    appointment.Status = AppointmentStatus.NoShow;
                                    break;
                                default:
                                    break;
                            }

                            //Participant - Patient
                            appointment.Participants.Add(new Participant()
                            {
                                AccountId = patient.AccountId,
                                AppointmentId = appointment.AppointmentId,
                                Type = ParticipantType.Patient,
                                PatientId = patient.PatientId,
                                Required = true
                            });

                            //Participant - Practitionar
                            appointment.Participants.Add(new Participant()
                            {
                                AccountId = patient.AccountId,
                                AppointmentId = appointment.AppointmentId,
                                Type = ParticipantType.Provider,
                                DoctorId = doctor.DoctorId,
                                Required = true
                            });
                            //Participant - Location
                            appointment.Participants.Add(new Participant()
                            {
                                AccountId = patient.AccountId,
                                AppointmentId = appointment.AppointmentId,
                                Type = ParticipantType.Location,
                                LocationId = location.LocationId,
                                Required = true
                            });

                            newAppointments.Add(appointment);
                            updatedAppointments.Add(item);
                        }
                    }
                }
            }
            if (newAppointments.Count > 0)
            {
                _appointmentServices.CreateAppointmentFromCerner(newAppointments);
                foreach (var item in updatedAppointments)
                {
                    UpdateAppointment(item);
                }
            }
            return true;
        }

        public bool CreatePatientAppointments(string cernerPatientId)
        {
            var appointments = _uow.GetRepository<CernerAppointment>().Queryable().Where(x => x.IsIntegrated == false && x.PatientId == cernerPatientId);
            if (appointments != null && appointments.Count() > 0)
            {
                CreateChargeCaptureAppointments(appointments.ToList());
                return true;
            }
            return false;
        }
    }
}
