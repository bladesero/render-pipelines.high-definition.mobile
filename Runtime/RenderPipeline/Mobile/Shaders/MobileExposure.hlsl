#ifndef MOBILE_EXPOSURE_INCLUDED
#define MOBILE_EXPOSURE_INCLUDED
float _MobileExposure, _MobileAutomaticExposure;
TEXTURE2D(_MobileExposureTexture);
float MobileCurrentExposure()
{
    return _MobileAutomaticExposure > 0 ? LOAD_TEXTURE2D(_MobileExposureTexture, int2(0, 0)).r : _MobileExposure;
}
#endif
