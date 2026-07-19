import { Component, signal, OnInit, inject } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { EmployeeService, Employee } from '../../services/employee.service';
import { FeatureFlagService } from '../../services/feature-flag.service';
import { AuthService } from '../../services/auth.service';
import { ToastService } from '../../services/toast.service';
import { ApiErrorService } from '../../services/api-error.service';
import { normaliseFile } from '../../utils/image-utils';

@Component({
  selector: 'app-employee-detail',
  imports: [NavbarComponent, FormsModule, RouterLink],
  templateUrl: './employee-detail.page.html'
})
export class EmployeeDetailPage implements OnInit {
  flags = inject(FeatureFlagService);
  auth = inject(AuthService);
  employee = signal<Employee | null>(null);
  firstName = '';
  lastName = '';
  phoneNumber = '';
  address = '';
  city = '';
  isActive = true;
  /** Hourly wage in EUR. Blank string means "no rate set yet". */
  hourlyWage: number | null = null;
  /** Employer contributions % of gross (odvody). Null = unset. */
  odvodyPct: number | null = null;
  /** Company division (Fáza D8) — drives the division-scoped Mzdy view. */
  division = 'profistav';
  /** Free-text pozícia (F6): "šofér", "murár"… */
  position = '';
  currentPin = signal<string | null>(null);
  showCurrentPin = signal(false);
  newPin = '';
  pinSaved = false;
  photoPreview = signal<string | null>(null);
  isDragOver = signal(false);
  private id = 0;

  get pinValid() { return /^\d{4,6}$/.test(this.newPin); }

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private employeeService: EmployeeService,
    private toast: ToastService,
    private apiError: ApiErrorService
  ) {}

  ngOnInit() {
    this.id = Number(this.route.snapshot.paramMap.get('id'));
    this.employeeService.get(this.id).subscribe(emp => {
      this.employee.set(emp);
      this.firstName = emp.firstName;
      this.lastName = emp.lastName;
      this.phoneNumber = emp.phoneNumber ?? '';
      this.address = emp.address ?? '';
      this.city = emp.city ?? '';
      this.isActive = emp.isActive;
      this.hourlyWage = emp.hourlyWage ?? null;
      this.odvodyPct = emp.odvodyPct ?? null;
      this.division = emp.division === 'stroje' ? 'stroje' : 'profistav';
      this.position = emp.position ?? '';
      this.photoPreview.set(emp.photoUrl ?? null);
      this.currentPin.set(emp.pinPlain ?? null);
    });
  }

  onSave() {
    this.employeeService.update(this.id, {
      firstName: this.firstName,
      lastName: this.lastName,
      phoneNumber: this.phoneNumber || undefined,
      address: this.address || undefined,
      city: this.city || undefined,
      isActive: this.isActive,
      hourlyWage: this.hourlyWage,
      odvodyPct: this.odvodyPct,
      division: this.division,
      position: this.position
    }).subscribe({
      next: () => {
        this.toast.success('Zmeny uložené');
        this.router.navigate(['/admin/employees']);
      },
      error: e => this.toast.error(this.apiError.friendly(e, 'Uloženie zamestnanca zlyhalo'))
    });
  }

  onSetPin() {
    if (!/^\d{4,6}$/.test(this.newPin)) return;
    const savedPin = this.newPin;
    this.employeeService.setPin(this.id, savedPin).subscribe({
      next: () => {
        this.currentPin.set(savedPin);
        this.pinSaved = true;
        this.newPin = '';
        setTimeout(() => this.pinSaved = false, 3000);
      },
      error: e => {
        if (e.status === 409) {
          this.toast.error('Tento PIN je už priradený inému zamestnancovi. Prosím zvoľte iný PIN.');
        } else {
          this.toast.error(this.apiError.friendly(e, 'PIN sa nepodarilo zmeniť'));
        }
      }
    });
  }

  onFileSelected(event: Event) {
    const file = (event.target as HTMLInputElement).files?.[0];
    if (file) this.processPhotoFile(file);
  }

  onDragOver(event: DragEvent) {
    event.preventDefault();
    this.isDragOver.set(true);
  }

  onDragLeave() {
    this.isDragOver.set(false);
  }

  onDrop(event: DragEvent) {
    event.preventDefault();
    this.isDragOver.set(false);
    const file = event.dataTransfer?.files?.[0];
    if (file) this.processPhotoFile(file);
  }

  async processPhotoFile(file: File): Promise<void> {
    if (!file.type.startsWith('image/') && !file.name.match(/\.(heic|heif)$/i)) return;
    const normalised = await normaliseFile(file);   // HEIC → PNG before canvas decode
    const resized = await this.resizeImage(normalised);
    this.employeeService.uploadPhoto(this.id, resized).subscribe({
      next: url => {
        this.employee.update(e => e ? { ...e, photoUrl: url } : e);
        this.photoPreview.set(url);
      },
      error: e => this.toast.error(this.apiError.friendly(e, 'Nahranie fotografie zlyhalo'))
    });
  }

  private resizeImage(file: File, maxDimension = 800): Promise<File> {
    return new Promise(resolve => {
      const img = new Image();
      const objectUrl = URL.createObjectURL(file);
      img.onload = () => {
        URL.revokeObjectURL(objectUrl);
        const scale = Math.min(1, maxDimension / Math.max(img.width, img.height));
        const w = Math.round(img.width * scale);
        const h = Math.round(img.height * scale);
        const canvas = document.createElement('canvas');
        canvas.width = w;
        canvas.height = h;
        canvas.getContext('2d')!.drawImage(img, 0, 0, w, h);
        canvas.toBlob(
          blob => resolve(new File([blob!], file.name.replace(/\.[^.]+$/, '.jpg'), { type: 'image/jpeg' })),
          'image/jpeg',
          0.85
        );
      };
      img.src = objectUrl;
    });
  }

  onBack() {
    this.router.navigate(['/admin/employees']);
  }
}
