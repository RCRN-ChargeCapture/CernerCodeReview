using RCRNChargeCapture.Models.Cerner;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RCRNChargeCapture.Integration.Cerner
{
    public interface ICernerAppointmentServices
    {
        long CreateAppointment(CernerAppointment appointmentEntity);
        List<CernerAppointment> CreateAppointment(List<CernerAppointment> appointmentList);
        bool UpdateAppointment(CernerAppointment appointmentEntity);
        CernerAppointment GetAppointmentByCernerId(string cernerAppointmentId);
        bool CreateChargeCaptureAppointments(List<CernerAppointment> appointments);
        bool CreatePatientAppointments(string cernerPatientId);
    }
}
