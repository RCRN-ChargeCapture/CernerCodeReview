using RCRNChargeCapture.Models.Cerner;
using RCRNChargeCapture.Models.Charge;
using RCRNChargeCapture.ViewModel.APIIntegration.Cerner;
using System.Collections.Generic;

namespace RCRNChargeCapture.Integration.Cerner
{
    public interface ICernerPatientServices
    {
        CernerPatient GetPatientByCernerId(string cernerPatientId);
        CernerPatient GetPatientById(long patientId);
        long CreatePatient(CernerPatient patientEntity);
        long CreateCernerPatientToChargeCapture(long cernerPatientId, string cernerLocationId);
        List<CernerPatient> CreatePatient(List<CernerPatient> patientList);
        bool UpdatePatient(CernerPatient patientEntity);
        bool DeletePatient(long patientId);
        List<Patient> GetUnMappedPatients();
        bool SyncPatients(List<CernerPatientViewModel> patientList);

        CernerPatientSearchResult GetCernerPatients(string cernerLocationId);
    }
}
