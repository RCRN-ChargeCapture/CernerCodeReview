using RCRNChargeCapture.ViewModel.APIIntegration.Cerner;
using System.Collections.Generic;

namespace RCRNChargeCapture.Integration.Cerner
{
    public interface ICernerIntegrationServices
    {
        bool Sync();
        List<CernerLocationViewModel> GetCernerLocations();
    }
}
